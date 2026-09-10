using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Pinboard.Events
{
    /// <summary>
    /// Handles a jump request coming from the board window. Board windows
    /// stay open outside of any command, so they cannot call the Revit API
    /// directly, this handler is the doorbell they ring instead, Revit
    /// calls Execute on its own terms once it is safe to touch the model.
    ///
    /// TargetElementId is optional. When set, the matching element also
    /// gets selected and scrolled to inside the schedule. When left null,
    /// this just opens the schedule itself, used for double clicking a
    /// card's header to jump straight into the real schedule regardless of
    /// whether row to element mapping is available for it.
    /// </summary>
    public class JumpToElementHandler : IExternalEventHandler
    {
        public ElementId ScheduleId { get; set; }
        public ElementId TargetElementId { get; set; }

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                return;
            }

            Document doc = uidoc.Document;
            ViewSchedule schedule = doc.GetElement(ScheduleId) as ViewSchedule;
            if (schedule == null)
            {
                TaskDialog.Show("Pinboard", "That schedule no longer exists in this project.");
                return;
            }

            try
            {
                uidoc.ActiveView = schedule;
            }
            catch
            {
                TaskDialog.Show("Pinboard", "Could not open that schedule view.");
                return;
            }

            if (TargetElementId != null)
            {
                List<ElementId> idsToSelect = new List<ElementId> { TargetElementId };
                uidoc.Selection.SetElementIds(idsToSelect);
                uidoc.ShowElements(TargetElementId);
            }
        }

        public string GetName()
        {
            return "Pinboard Jump To Element";
        }
    }
}
