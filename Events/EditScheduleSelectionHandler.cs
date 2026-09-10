using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Pinboard.Storage;
using Pinboard.UI;

namespace Pinboard.Events
{
    /// <summary>
    /// Handles the whole "edit which schedules are on this board" flow in
    /// one safe pass, listing schedules, showing the picker, saving the
    /// updated batch, and telling the board to add or remove cards to
    /// match, all inside the Revit sanctioned moment this doorbell exists
    /// for. The board window itself never touches the Revit API directly
    /// for any of this.
    /// </summary>
    public class EditScheduleSelectionHandler : IExternalEventHandler
    {
        public PinboardBoardWindow TargetBoard { get; set; }

        public void Execute(UIApplication app)
        {
            if (TargetBoard == null)
            {
                return;
            }

            Document doc = TargetBoard.Document;

            List<ViewSchedule> allSchedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate)
                .OrderBy(s => s.Name)
                .ToList();

            HashSet<ElementId> currentIds = TargetBoard.GetCurrentScheduleIds();

            EditScheduleSelectionWindow window = new EditScheduleSelectionWindow(allSchedules, currentIds);
            bool? dialogResult = window.ShowDialog();

            if (dialogResult != true)
            {
                return;
            }

            List<ElementId> newIds = window.SelectedScheduleIds;

            using (Transaction t = new Transaction(doc, "Update Pinboard batch schedules"))
            {
                t.Start();
                BatchStorageService.SaveBatch(doc, TargetBoard.BatchName, newIds);
                t.Commit();
            }

            TargetBoard.ApplyScheduleSelection(doc, newIds);
        }

        public string GetName()
        {
            return "Pinboard Edit Board Schedules";
        }
    }
}
