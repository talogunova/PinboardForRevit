using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Pinboard.UI;

namespace Pinboard.Events
{
    /// <summary>
    /// The doorbell for refreshing an already open board. A debounce timer
    /// on the board window fires this after changes settle down, at which
    /// point we are back inside a Revit sanctioned moment and it is safe to
    /// reread the affected schedules.
    /// </summary>
    public class RefreshBoardHandler : IExternalEventHandler
    {
        public PinboardBoardWindow TargetBoard { get; set; }

        public void Execute(UIApplication app)
        {
            if (TargetBoard == null)
            {
                return;
            }

            Document doc = TargetBoard.Document;
            TargetBoard.PerformRefresh(doc);
        }

        public string GetName()
        {
            return "Pinboard Refresh Board";
        }
    }
}
