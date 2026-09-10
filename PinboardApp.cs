using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Pinboard.Events;

namespace Pinboard
{
    public class PinboardApp : IExternalApplication
    {
        private static JumpToElementHandler _jumpHandler;
        private static ExternalEvent _jumpEvent;
        private static RefreshBoardHandler _refreshHandler;
        private static ExternalEvent _refreshEvent;
        private static SaveMarkupHandler _saveMarkupHandler;
        private static ExternalEvent _saveMarkupEvent;
        private static EditScheduleSelectionHandler _editSchedulesHandler;
        private static ExternalEvent _editSchedulesEvent;

        private static PushButton _selectButton;
        private static PushButton _openButton;
        private static UIControlledApplication _application;

        /// <summary>
        /// Called by a board window to jump to an element on the model.
        /// Safe to call from a modeless window, this just hands the request
        /// off to Revit through the registered ExternalEvent.
        /// </summary>
        public static void RequestJumpToElement(ElementId scheduleId, ElementId elementId)
        {
            _jumpHandler.ScheduleId = scheduleId;
            _jumpHandler.TargetElementId = elementId;
            _jumpEvent.Raise();
        }

        /// <summary>
        /// Called when a card's header is double clicked, opens the real
        /// schedule without needing a specific element, so this works even
        /// on schedules where row to element mapping is not available.
        /// </summary>
        public static void RequestOpenSchedule(ElementId scheduleId)
        {
            _jumpHandler.ScheduleId = scheduleId;
            _jumpHandler.TargetElementId = null;
            _jumpEvent.Raise();
        }

        /// <summary>
        /// Called by a board window's debounce timer once changes have
        /// settled down, hands off to Revit through the registered
        /// ExternalEvent so the actual reread happens in a safe context.
        /// </summary>
        public static void RequestRefresh(UI.PinboardBoardWindow board)
        {
            _refreshHandler.TargetBoard = board;
            _refreshEvent.Raise();
        }

        /// <summary>
        /// Called by a board window's markup debounce timer once drawing or
        /// erasing settles down, hands off to Revit through the registered
        /// ExternalEvent so the actual save happens in a safe context.
        /// </summary>
        public static void RequestSaveMarkup(Document doc, string batchName, byte[] inkBytes)
        {
            _saveMarkupHandler.Document = doc;
            _saveMarkupHandler.BatchName = batchName;
            _saveMarkupHandler.InkBytes = inkBytes;
            _saveMarkupEvent.Raise();
        }

        /// <summary>
        /// Called by the board's Edit button, hands the whole "list
        /// schedules, show the picker, save, update the board" flow off to
        /// Revit through the registered ExternalEvent, since none of that
        /// is safe to do directly from a modeless window.
        /// </summary>
        public static void RequestEditSchedules(UI.PinboardBoardWindow board)
        {
            _editSchedulesHandler.TargetBoard = board;
            _editSchedulesEvent.Raise();
        }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                _application = application;
                string tabName = "Pinboard";
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                application.CreateRibbonTab(tabName);

                RibbonPanel boardPanel = application.CreateRibbonPanel(tabName, "Board");

                // Select Schedules button, picks schedules and saves a named batch
                PushButtonData selectData = new PushButtonData(
                    "Pinboard_SelectSchedules",
                    "Select\nSchedules",
                    assemblyPath,
                    "Pinboard.Commands.SelectSchedulesCommand");
                selectData.ToolTip = "Pick schedules and save them as a named batch";

                // Open Board button, launches the floating board window
                PushButtonData openData = new PushButtonData(
                    "Pinboard_OpenBoard",
                    "Open\nBoard",
                    assemblyPath,
                    "Pinboard.Commands.OpenBoardCommand");
                openData.ToolTip = "Open the Pinboard window with a saved batch of schedules";

                // Best effort read for the very first icon set, corrected
                // for certain afterward through the ThemeChanged event.
                bool isDarkTheme = false;
                try
                {
                    isDarkTheme = UIThemeManager.CurrentTheme == UITheme.Dark;
                }
                catch
                {
                    // Older Revit API without UIThemeManager, just default to light.
                }

                string suffix = isDarkTheme ? "_dark" : "";
                selectData.LargeImage = LoadIcon($"select{suffix}.png");
                selectData.Image = LoadIcon($"select{suffix}_small.png");
                openData.LargeImage = LoadIcon($"open{suffix}.png");
                openData.Image = LoadIcon($"open{suffix}_small.png");

                _selectButton = boardPanel.AddItem(selectData) as PushButton;
                _openButton = boardPanel.AddItem(openData) as PushButton;

                _jumpHandler = new JumpToElementHandler();
                _jumpEvent = ExternalEvent.Create(_jumpHandler);

                _refreshHandler = new RefreshBoardHandler();
                _refreshEvent = ExternalEvent.Create(_refreshHandler);

                _saveMarkupHandler = new SaveMarkupHandler();
                _saveMarkupEvent = ExternalEvent.Create(_saveMarkupHandler);

                _editSchedulesHandler = new EditScheduleSelectionHandler();
                _editSchedulesEvent = ExternalEvent.Create(_editSchedulesHandler);

                application.ControlledApplication.DocumentChanged += OnDocumentChanged;
                application.ThemeChanged += OnThemeChanged;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Pinboard Error", $"Failed to initialize Pinboard:\n{ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            application.ThemeChanged -= OnThemeChanged;
            return Result.Succeeded;
        }

        /// <summary>
        /// Revit's own event for exactly this, fired after the theme
        /// actually changes, whether that happens live while Revit is
        /// running or is only settling in during startup. This replaces
        /// the guesswork of trying to catch the right moment ourselves.
        /// </summary>
        private static void OnThemeChanged(object sender, ThemeChangedEventArgs e)
        {
            bool isDarkTheme = false;
            try
            {
                isDarkTheme = UIThemeManager.CurrentTheme == UITheme.Dark;
            }
            catch
            {
                // Older Revit API without UIThemeManager, just keep the light set.
            }

            ApplyIconsForTheme(isDarkTheme);
        }

        private static void ApplyIconsForTheme(bool isDarkTheme)
        {
            string suffix = isDarkTheme ? "_dark" : "";

            if (_selectButton != null)
            {
                _selectButton.LargeImage = LoadIcon($"select{suffix}.png");
                _selectButton.Image = LoadIcon($"select{suffix}_small.png");
            }

            if (_openButton != null)
            {
                _openButton.LargeImage = LoadIcon($"open{suffix}.png");
                _openButton.Image = LoadIcon($"open{suffix}_small.png");
            }
        }

        /// <summary>
        /// Revit calls this itself right after a change commits, so this is
        /// a safe moment to do cheap reads. The actual schedule reread is
        /// deliberately not done here, it is deferred through each board's
        /// own debounce timer so a burst of edits collapses into one
        /// refresh instead of one per change.
        /// </summary>
        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            Document doc = e.GetDocument();

            List<ElementId> changedIds = new List<ElementId>();
            changedIds.AddRange(e.GetAddedElementIds());
            changedIds.AddRange(e.GetModifiedElementIds());
            changedIds.AddRange(e.GetDeletedElementIds());

            if (changedIds.Count == 0)
            {
                return;
            }

            foreach (UI.PinboardBoardWindow board in Events.BoardRegistry.GetBoardsForDocument(doc))
            {
                if (board.IsRelevantChange(doc, changedIds))
                {
                    board.NotifyPossibleChange();
                }
            }
        }

        /// <summary>
        /// Loads an icon from the Resources folder using a pack URI.
        /// The icon must be included as a Resource in the .csproj file.
        /// </summary>
        private static BitmapImage LoadIcon(string filename)
        {
            try
            {
                Uri uri = new Uri(
                    $"pack://application:,,,/Pinboard;component/Resources/{filename}");
                BitmapImage img = new BitmapImage(uri);
                return img;
            }
            catch
            {
                return null;
            }
        }
    }
}
