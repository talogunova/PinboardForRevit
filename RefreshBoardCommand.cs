using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Pinboard.Events;
using Pinboard.UI;

namespace Pinboard.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class RefreshBoardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            var boards = BoardRegistry.GetBoardsForDocument(doc);
            if (boards.Count == 0)
            {
                TaskDialog.Show("Pinboard", "No Pinboard window is open for this project right now.");
                return Result.Succeeded;
            }

            foreach (PinboardBoardWindow board in boards)
            {
                board.PerformRefresh(doc);
            }

            return Result.Succeeded;
        }
    }
}
