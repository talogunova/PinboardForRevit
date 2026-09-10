using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Pinboard.Data;
using Pinboard.Storage;
using Pinboard.UI;

namespace Pinboard.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class OpenBoardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            List<ScheduleBatch> batches = BatchStorageService.GetAllBatches(doc);

            if (batches.Count == 0)
            {
                TaskDialog.Show("Pinboard",
                    "No saved batches yet. Use Select Schedules first to pick and name a batch.");
                return Result.Succeeded;
            }

            BatchPickerWindow picker = new BatchPickerWindow(doc, batches);
            bool? pickerResult = picker.ShowDialog();

            if (pickerResult != true || picker.SelectedBatch == null)
            {
                // Revit discards every change made during a command whose
                // Execute returns Cancelled, even ones already committed
                // earlier in this same run. If the person deleted a batch
                // but did not go on to open one, that deletion is real and
                // must be reported as success, or Revit would quietly undo
                // it the moment this command ends.
                return picker.AnyBatchesDeleted ? Result.Succeeded : Result.Cancelled;
            }

            ScheduleBatch batch = picker.SelectedBatch;

            // If this exact batch is already open in a board somewhere,
            // just bring that window forward instead of creating a
            // duplicate that would drift out of sync with the first one.
            PinboardBoardWindow existingBoard = Events.BoardRegistry.FindBoardForBatch(doc, batch.Name);
            if (existingBoard != null)
            {
                if (existingBoard.WindowState == System.Windows.WindowState.Minimized)
                {
                    existingBoard.WindowState = System.Windows.WindowState.Normal;
                }
                existingBoard.Activate();
                return Result.Succeeded;
            }

            System.Windows.Media.Color backgroundColor;
            try
            {
                using (ColorOptions colorOptions = ColorOptions.GetColorOptions())
                {
                    Autodesk.Revit.DB.Color revitColor = colorOptions.BackgroundColor;
                    backgroundColor = System.Windows.Media.Color.FromRgb(revitColor.Red, revitColor.Green, revitColor.Blue);
                }
            }
            catch
            {
                // Older Revit API without ColorOptions, fall back to a plain light gray.
                backgroundColor = System.Windows.Media.Color.FromRgb(0xEC, 0xEC, 0xEC);
            }

            List<ScheduleCardViewModel> cards = batch.ScheduleIds
                .Select(id => doc.GetElement(id) as ViewSchedule)
                .Where(schedule => schedule != null)
                .Select((schedule, index) => BuildCard(schedule, index))
                .ToList();

            byte[] initialMarkup = MarkupStorageService.LoadMarkup(doc, batch.Name);

            PinboardBoardWindow board = new PinboardBoardWindow(doc, batch.Name, cards, backgroundColor, initialMarkup);
            board.Show();

            return Result.Succeeded;
        }

        /// <summary>
        /// Builds a card for one schedule. If reading its data fails, the
        /// card still shows up with the schedule name and a one row table
        /// carrying the error, instead of taking down the whole board.
        /// Cards start out arranged in a loose grid, three per row, so they
        /// do not all land stacked on top of each other, from there the
        /// user can drag them wherever they want. The starting spot is
        /// rounded to a multiple of the board's own grid spacing so a
        /// fresh card lines up with the dots immediately.
        /// </summary>
        private const double GridSize = 19;

        private static ScheduleCardViewModel BuildCard(ViewSchedule schedule, int index)
        {
            // Offset well away from the board's true top left corner
            // (world coordinate zero, zero), which is a hard edge nothing
            // can exist above or to the left of, cards, strokes, or the
            // selection box included. Starting the first card here instead
            // of right on that corner leaves real room in every direction.
            const double baseX = 5000;
            const double baseY = 3000;

            double rawX = baseX + (index % 3) * 360;
            double rawY = baseY + (index / 3) * 340;
            double startX = Math.Round(rawX / GridSize) * GridSize;
            double startY = Math.Round(rawY / GridSize) * GridSize;

            try
            {
                ScheduleExtractResult extractResult = ScheduleDataExtractor.Extract(schedule, schedule.Document);
                return new ScheduleCardViewModel
                {
                    Name = schedule.Name,
                    ScheduleId = schedule.Id,
                    CategoryId = schedule.Definition.CategoryId,
                    DataView = extractResult.Table.DefaultView,
                    X = startX,
                    Y = startY
                };
            }
            catch (Exception ex)
            {
                DataTable errorTable = new DataTable();
                errorTable.Columns.Add("Error", typeof(string));
                errorTable.Rows.Add($"Could not read this schedule: {ex.Message}");

                return new ScheduleCardViewModel
                {
                    Name = schedule.Name,
                    ScheduleId = schedule.Id,
                    CategoryId = schedule.Definition.CategoryId,
                    DataView = errorTable.DefaultView,
                    X = startX,
                    Y = startY
                };
            }
        }
    }
}
