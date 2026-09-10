# Pinboard

A Revit add in that lets you pull several schedules onto one floating board, arrange them side by side, and compare them at a glance instead of flipping between browser tabs one schedule at a time.

## What it does

Pinboard adds its own tab to the Revit ribbon with two buttons. You pick a handful of schedules, save that pick as a named batch, and open it as a floating window with a card for every schedule in it. Each card shows the real rows and columns from that schedule. Cards can be dragged and resized freely, the whole board can be zoomed and panned, and you can sketch directly on top of everything with a pen.

## Features

- Select Schedules, check off the schedules you want and save them as a named batch, stored inside the project file itself using Extensible Storage, so it travels with the model
- Open Board, pick a saved batch and it opens as a floating window, separate from Revit's own docking system
- Delete a saved batch straight from that picker with a trash icon on each row
- Opening a batch that already has a window open just brings that window forward instead of opening a second copy
- Each schedule shows up as its own card with a real read only grid of that schedule's data
- Drag a card by its header, resize it from any edge or the corner, everything snaps to a dotted grid that pans and zooms with the board and never runs out no matter how far you go
- Scroll wheel zooms, middle mouse drag pans, middle mouse double click fits everything on screen at once, same controls Revit's own 3D view already uses
- Double click a card's header to jump straight into that real schedule in Revit
- Pen and eraser for sketching markup on the board, with a small color and thickness panel that appears while the pen is active and tucks itself away the moment you start drawing. Markup saves automatically with the batch and reloads exactly as you left it
- The board watches the model and refreshes affected cards on its own a moment after something relevant changes
- Matches whatever background color is set in Revit's own Options, Graphics, Colors, so the board looks right whether Revit is in light or dark theme

## Requirements

- Revit 2025
- Windows, net8.0 windows target, x64

## Building it

Open `Pinboard.csproj` in Visual Studio as a project, not as a folder. Build with Ctrl B. Output copies straight to `AppData\Roaming\Autodesk\Revit\Addins\2025\Pinboard\`, and the manifest sitting at `Addins\2025\Pinboard.addin` points Revit at it.

## Using it

1. Click Select Schedules, check the schedules you want to compare, give the batch a name, save it
2. Click Open Board, pick a saved batch from the list, or delete one you no longer need right there
3. Drag, resize, zoom, and pan the board however you like
4. Double click a card's header to jump straight to that real schedule in Revit
5. Click Pen to sketch on the board, pick a color and thickness from the panel that shows up, click Eraser to remove a stroke
6. Leave it running, it refreshes itself as the model changes
7. Click "Hamburger" to edit current board

## Known limitations

- Escape returns focus to Revit's main window and can push the board behind it, same as it does for other floating tool windows next to Revit. Nothing closes and nothing is lost, but it can be surprising the first time
- Each card shows a plain reshaped grid of the schedule's data, not the live formatted Revit schedule view itself
- A schedule with a lot of columns can render wider than its card; resizing the card to fit is the workaround for now

## Built with

Revit API, C# targeting net8.0 windows, WPF, and Extensible Storage for saved batches and markup.
