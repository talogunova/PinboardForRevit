using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Autodesk.Revit.DB;

namespace Pinboard.Data
{
    /// <summary>
    /// The extracted table plus, when it can be trusted, which element each
    /// row belongs to. RowElementIds is null when the schedule's structure
    /// makes that mapping unreliable, callers should treat null as jump to
    /// element not being available for that schedule.
    /// </summary>
    public class ScheduleExtractResult
    {
        public DataTable Table { get; set; }
        public List<ElementId> RowElementIds { get; set; }
    }

    /// <summary>
    /// Pulls the visible rows and columns out of a live Revit schedule and
    /// reshapes them into a plain DataTable the WPF DataGrid can bind to.
    ///
    /// This reads a snapshot of the schedule at the moment it is called, it
    /// does not stay connected to the schedule. Simple element and material
    /// take off schedules map cleanly. Schedules with grouping, subtotals or
    /// merged header rows are read on a best effort basis, the shape mostly
    /// comes through but grouped header labels may repeat or blank out.
    ///
    /// Revit does not put a schedule's real column labels in a fixed spot.
    /// Sometimes they sit in the header section, sometimes the header
    /// section only holds the schedule's title banner and the real labels
    /// are actually the first row of the body section. This looks in both
    /// places and picks whichever row actually looks like a set of distinct
    /// column labels rather than one merged title cell.
    /// </summary>
    public static class ScheduleDataExtractor
    {
        public static ScheduleExtractResult Extract(ViewSchedule schedule, Document doc)
        {
            TableData tableData = schedule.GetTableData();
            TableSectionData bodySection = tableData.GetSectionData(SectionType.Body);
            TableSectionData headerSection = tableData.GetSectionData(SectionType.Header);

            int columnCount = bodySection.NumberOfColumns;
            if (columnCount == 0 && headerSection != null)
            {
                columnCount = headerSection.NumberOfColumns;
            }

            (List<string> columnNames, int bodyStartRow) = ResolveHeaders(
                schedule, headerSection, bodySection, columnCount);

            DataTable table = new DataTable();
            foreach (string columnName in columnNames)
            {
                table.Columns.Add(columnName, typeof(string));
            }

            for (int row = bodyStartRow; row < bodySection.NumberOfRows; row++)
            {
                List<string> cells = ReadRow(schedule, SectionType.Body, row, columnCount);
                DataRow dataRow = table.NewRow();
                for (int col = 0; col < columnCount; col++)
                {
                    dataRow[col] = cells[col];
                }
                table.Rows.Add(dataRow);
            }

            return new ScheduleExtractResult
            {
                Table = table
            };
        }

        /// <summary>
        /// Finds the row that actually looks like column labels, checking
        /// the header section first (from the row closest to the data
        /// upward), then falling back to the body section's first row.
        /// Returns the resolved names plus which body row to start reading
        /// real data from, 0 normally, 1 if the labels turned out to be
        /// sitting in the body section instead of the header section.
        /// </summary>
        private static (List<string> names, int bodyStartRow) ResolveHeaders(
            ViewSchedule schedule, TableSectionData headerSection, TableSectionData bodySection, int columnCount)
        {
            if (headerSection != null)
            {
                for (int row = headerSection.NumberOfRows - 1; row >= 0; row--)
                {
                    List<string> candidate = ReadRow(schedule, SectionType.Header, row, columnCount);
                    if (LooksLikeLabelRow(candidate))
                    {
                        return (Deduplicate(candidate, columnCount), 0);
                    }
                }
            }

            if (bodySection != null && bodySection.NumberOfRows > 0)
            {
                List<string> candidate = ReadRow(schedule, SectionType.Body, 0, columnCount);
                if (LooksLikeLabelRow(candidate))
                {
                    return (Deduplicate(candidate, columnCount), 1);
                }
            }

            List<string> generic = new List<string>();
            for (int col = 0; col < columnCount; col++)
            {
                generic.Add($"Col {col + 1}");
            }
            return (generic, 0);
        }

        /// <summary>
        /// A real label row has more than one distinct filled in cell. A
        /// merged title banner only fills the first column and leaves the
        /// rest blank, which this rejects.
        /// </summary>
        private static bool LooksLikeLabelRow(List<string> cells)
        {
            int nonEmptyCount = cells.Count(c => !string.IsNullOrWhiteSpace(c));
            return nonEmptyCount >= 2;
        }

        private static List<string> ReadRow(ViewSchedule schedule, SectionType section, int row, int columnCount)
        {
            List<string> cells = new List<string>();
            for (int col = 0; col < columnCount; col++)
            {
                string text;
                try
                {
                    text = schedule.GetCellText(section, row, col);
                }
                catch
                {
                    text = "";
                }
                cells.Add(text ?? "");
            }
            return cells;
        }

        private static List<string> Deduplicate(List<string> rawNames, int columnCount)
        {
            HashSet<string> used = new HashSet<string>();
            List<string> finalNames = new List<string>();

            for (int col = 0; col < columnCount; col++)
            {
                string name = col < rawNames.Count ? rawNames[col] : "";
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"Col {col + 1}";
                }

                string candidate = name;
                int suffix = 2;
                while (used.Contains(candidate))
                {
                    candidate = $"{name} ({suffix})";
                    suffix++;
                }
                used.Add(candidate);
                finalNames.Add(candidate);
            }

            return finalNames;
        }
    }
}
