using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;

namespace Pinboard.UI
{
    /// <summary>
    /// Same checklist idea as picking schedules for a new batch, except
    /// this pre checks whatever is already on the board and just hands
    /// back the full new selection, it does not touch storage itself,
    /// that happens in the caller.
    /// </summary>
    public partial class EditScheduleSelectionWindow : Window
    {
        private readonly List<ScheduleItemViewModel> _items = new List<ScheduleItemViewModel>();

        public List<ElementId> SelectedScheduleIds { get; private set; } = new List<ElementId>();

        public EditScheduleSelectionWindow(List<ViewSchedule> allSchedules, HashSet<ElementId> currentlySelectedIds)
        {
            InitializeComponent();

            foreach (ViewSchedule schedule in allSchedules)
            {
                _items.Add(new ScheduleItemViewModel
                {
                    Id = schedule.Id,
                    Name = schedule.Name,
                    IsChecked = currentlySelectedIds.Contains(schedule.Id)
                });
            }

            ScheduleList.ItemsSource = _items;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedScheduleIds = _items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
