# Click Counter

A small Windows tool that counts clicks and shows the count in the center of the screen, on top of every other window. The overlay is click-through and never takes focus.

## Build

Run `build.cmd`. It uses the C# compiler that ships with Windows, so nothing needs to be installed. The output is `ClickCounter.exe` in the same folder.

## Use

1. Start `ClickCounter.exe`. The settings window opens and the overlay shows a dash.
2. Press the start trigger (default: mouse button 4). The overlay shows 0.
3. Every count trigger (default: left click) adds one.
4. From the alert value on, the number turns the alert color.
5. When the count reaches the max value it goes back to 0 and the next loop begins.
6. Pressing the start trigger at any time resets to 0.

## Settings

Everything is on the one settings window and saves as you type.

| Setting | Meaning |
| --- | --- |
| Count trigger | Click Set, then press any key or mouse button. |
| Start / reset trigger | Same. Esc cancels the capture. |
| Max count | The count resets to 0 when it reaches this value. |
| Alert from count | The number turns the alert color from this value on. 0 disables the alert. |
| Overlay font size | Size of the number on screen. |
| Normal color, Alert color | Any WPF color name or hex value, for example `#FF3B30` or `Yellow`. |
| Count clicks before the start trigger | Off by default, so nothing counts until the start trigger is pressed once. |
| Show overlay | Hides or shows the number. |

Clicks and key presses on the settings window itself are ignored, so changing settings does not change the count.

Settings live in `%APPDATA%\ClickCounter\settings.txt`.

## Limits

Games that run in exclusive full-screen mode draw straight to the display and hide every overlay, including this one. Borderless or windowed mode works.
