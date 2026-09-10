using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Pinboard.Storage;
using Pinboard.UI;

namespace Pinboard.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SelectSchedulesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            SelectSchedulesWindow window = new SelectSchedulesWindow(doc);
            bool? dialogResult = window.ShowDialog();

            if (dialogResult != true)
            {
                return Result.Cancelled;
            }

            try
            {
                using (Transaction t = new Transaction(doc, "Save Pinboard batch"))
                {
                    t.Start();
                    BatchStorageService.SaveBatch(doc, window.SavedBatchName, window.SavedScheduleIds);
                    t.Commit();
                }

                TaskDialog.Show("Pinboard",
                    $"Saved batch \"{window.SavedBatchName}\" with {window.SavedScheduleIds.Count} schedule(s).");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
