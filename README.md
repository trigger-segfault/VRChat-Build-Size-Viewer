# VRChat Build Size Viewer
A script for viewing the build size for VRC worlds and avatars after building. It's kinda ugly. So sorry about that. Made it for myself, but thought it might be handy for others! -MunifiSense

It's now less ugly, but over-engineered for the sake of virtualization and reduced layout calls, so sorry about that. -trigger\_segfault

## How To Use
* Place the file `BuildSizeViewer.cs` into your Unity project in `Assets/Editor/` (or under any `Editor/` folder).
* Open the **Build Size** window by going to **Window** &gt; **Trigger Segfault** &gt; **VRC Build Size Viewer**.
* Build a world or avatar in any Unity editor, either for testing or upload. Note that Play Mode **will not** produce valid build logs!
* Press **Read Build Logs** to read a maximum number of build logs from the current and previous editor logs. Note that the Unity editor will clear editor logs every so often, so build log history will only be remembered from the current and previous sessions (supposedly).
* Select which build log to view using the **Select Build Log** dropdown. Most-recent build logs are higher in the list. There's no timestamps available in the editor log, so sadly it's not possible to identify when a build log was created.
* *TADA.*

### Other Features
* Support for reading build logs on Windows, OSX, and Linux.
* Icons are shown next to file paths if the file still exists (which it may not if the asset was processed by [Non-Destructive Modular Framework](https://github.com/bdunderscore/ndmf)).
* Click on a file path to select the file in the Inspector and navigate to it in the Project window.
* Click on any column header for unidirectional sorting.

### Preferences
* **Language:** Select from available languages **(No other languages available yet, so the dropdown is hidden)!**
* **Show Categories:** Can be used to hide the Categories list, which takes up an excessive amount of vertical real estate.
* **Enable Hover:** Can be used to disable hover events (what causes file paths and column headers to light up when moused over), which improves performance by reducing the number of window repaints.
* **Max Build Logs:** Control how many build logs will be stored in memory after pressing the **Read Build Logs** button. This does not retroactively shrink the number of previously-read build logs.

## Image Preview

<img width="630" height="905" alt="Image of VRC Build Size Viewer" src="https://github.com/user-attachments/assets/43481478-045d-45cf-851f-16b7e70b8ada" />
