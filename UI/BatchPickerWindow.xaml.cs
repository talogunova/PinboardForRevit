using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Pinboard.Storage;
using Color = System.Windows.Media.Color;

namespace Pinboard.UI
{
    /// <summary>
    /// Lets the user pick one saved batch to open on the board, or delete
    /// one they no longer need straight from the list.
    /// </summary>
    public partial class BatchPickerWindow : Window
    {
        private readonly Document _doc;
        private readonly ObservableCollection<ScheduleBatch> _batches;

        public ScheduleBatch SelectedBatch { get; private set; }

        /// <summary>
        /// True once at least one batch has actually been deleted. Revit
        /// rolls back every change made during a command if that command's
        /// own return value is Cancelled, even changes already committed
        /// earlier in the same run, so the command needs to know a real
        /// change happened here in order to report success instead, even
        /// when the person closes this picker without opening a board.
        /// </summary>
        public bool AnyBatchesDeleted { get; private set; }

        public BatchPickerWindow(Document doc, System.Collections.Generic.List<ScheduleBatch> batches)
        {
            InitializeComponent();
            _doc = doc;
            _batches = new ObservableCollection<ScheduleBatch>(batches);
            BatchListBox.ItemsSource = _batches;
            if (_batches.Count > 0)
            {
                BatchListBox.SelectedIndex = 0;
            }

            ApplyTheme();
        }

        /// <summary>
        /// Matches whichever theme Revit is currently set to, so the
        /// delete icon reads as plain text rather than standing out in a
        /// fixed color that might not suit a dark background.
        /// </summary>
        private void ApplyTheme()
        {
            bool isDarkTheme = false;
            try
            {
                isDarkTheme = UIThemeManager.CurrentTheme == UITheme.Dark;
            }
            catch
            {
                // Older Revit API without UIThemeManager, just keep the light set.
            }

            if (isDarkTheme)
            {
                Resources["PickerBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x22, 0x29, 0x33));
                Resources["PickerTextBrush"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
                Resources["PickerBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x55, 0x5C, 0x66));
            }
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedBatch = BatchListBox.SelectedItem as ScheduleBatch;
            if (SelectedBatch == null)
            {
                return;
            }

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Just closes the picker without opening a board. Kept separate
        /// from Cancel so closing after deleting a batch does not read as
        /// though it is undoing that deletion, deletions are already
        /// committed the moment you confirm them and are not affected by
        /// either button here.
        /// </summary>
        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// This dialog runs synchronously inside OpenBoardCommand's own
        /// Execute, so it is already in a safe Revit context, no
        /// ExternalEvent doorbell needed here like the board window needs
        /// for its own background actions.
        /// </summary>
        private void DeleteBatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not ScheduleBatch batch)
            {
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                $"Delete the batch \"{batch.Name}\"? This cannot be undone.",
                "Pinboard", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            using (Transaction t = new Transaction(_doc, "Delete Pinboard batch"))
            {
                t.Start();
                BatchStorageService.DeleteBatch(_doc, batch.Name);
                t.Commit();
            }

            AnyBatchesDeleted = true;
            _batches.Remove(batch);
        }
    }
}
