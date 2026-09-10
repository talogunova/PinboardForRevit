using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Pinboard.Events
{
    /// <summary>
    /// Keeps track of which Pinboard board windows are currently open, so
    /// the DocumentChanged handler knows which boards to notify, and for
    /// which document, since more than one project could theoretically be
    /// open at once.
    /// </summary>
    public static class BoardRegistry
    {
        private static readonly List<UI.PinboardBoardWindow> _openBoards = new List<UI.PinboardBoardWindow>();

        public static void Register(UI.PinboardBoardWindow board)
        {
            _openBoards.Add(board);
        }

        public static void Unregister(UI.PinboardBoardWindow board)
        {
            _openBoards.Remove(board);
        }

        public static List<UI.PinboardBoardWindow> GetBoardsForDocument(Document doc)
        {
            return _openBoards.Where(b => b.Document.Equals(doc)).ToList();
        }

        /// <summary>
        /// Finds an already open board for this exact batch, if there is
        /// one, so opening the same batch twice can just bring the
        /// existing window forward instead of creating a duplicate.
        /// </summary>
        public static UI.PinboardBoardWindow FindBoardForBatch(Document doc, string batchName)
        {
            return _openBoards.FirstOrDefault(b => b.Document.Equals(doc) && b.BatchName == batchName);
        }
    }
}
