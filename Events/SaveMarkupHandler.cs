using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Pinboard.Storage;

namespace Pinboard.Events
{
    /// <summary>
    /// Saves a batch's markup ink to the project file. Drawing happens in a
    /// modeless window with no active command, so this is routed through
    /// the ExternalEvent doorbell, same reasoning as the jump and refresh
    /// handlers.
    /// </summary>
    public class SaveMarkupHandler : IExternalEventHandler
    {
        public Document Document { get; set; }
        public string BatchName { get; set; }
        public byte[] InkBytes { get; set; }

        public void Execute(UIApplication app)
        {
            if (Document == null || string.IsNullOrEmpty(BatchName) || InkBytes == null)
            {
                return;
            }

            using (Transaction t = new Transaction(Document, "Save Pinboard markup"))
            {
                t.Start();
                MarkupStorageService.SaveMarkup(Document, BatchName, InkBytes);
                t.Commit();
            }
        }

        public string GetName()
        {
            return "Pinboard Save Markup";
        }
    }
}
