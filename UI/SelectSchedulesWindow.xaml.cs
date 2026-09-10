using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Pinboard.Storage;

namespace Pinboard.UI
{
    /// <summary>
    /// Lets the user check off which schedules belong to a batch and name it.
    /// Reading and writing to Extensible Storage happens in the caller, this
    /// window only collects the picks, it does not touch the document itself.
    /// </summary>
    public partial class SelectSchedulesWindow : Window
    {
        private readonly Document _doc;
        private readonly List<ScheduleItemViewModel> _items = new List<ScheduleItemViewModel>();

        public string SavedBatchName { get; private set; } = "";
        public List<ElementId> SavedScheduleIds { get; private set; } = new List<ElementId>();

        public SelectSchedulesWindow(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            LoadSchedules();
        }

        private void LoadSchedules()
        {
            List<ViewSchedule> schedules = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate)
                .OrderBy(s => s.Name)
                .ToList();

            foreach (ViewSchedule schedule in schedules)
            {
                _items.Add(new ScheduleItemViewModel
                {
                    Id = schedule.Id,
                    Name = schedule.Name,
                    IsChecked = false
                });
            }

            ScheduleList.ItemsSource = _items;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string name = BatchNameBox.Text.Trim();
            List<ElementId> checkedIds = _items.Where(i => i.IsChecked).Select(i => i.Id).ToList();

            if (string.IsNullOrEmpty(name))
            {
                StatusText.Text = "Give this batch a name before saving.";
                return;
            }

            if (checkedIds.Count == 0)
            {
                StatusText.Text = "Check at least one schedule before saving.";
                return;
            }

            bool nameAlreadyUsed = BatchStorageService.GetAllBatches(_doc).Any(b => b.Name == name);
            if (nameAlreadyUsed)
            {
                MessageBoxResult result = MessageBox.Show(
                    $"A batch named \"{name}\" already exists. Overwrite it?",
                    "Pinboard",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            SavedBatchName = name;
            SavedScheduleIds = checkedIds;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class ScheduleItemViewModel : INotifyPropertyChanged
    {
        public ElementId Id { get; set; }
        public string Name { get; set; } = "";

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
