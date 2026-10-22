GOOGLE DRIVE WIDGET

Turn the Desktop access switch on to make D:\dev\GoogleDrive available to Explorer and desktop applications.
Green means connected, gray means off, and amber means a transition is in progress.
Turning it off waits for fresh reports of no cached file activity or pending uploads, then requests a clean rclone exit.
Save and CLOSE all Drive documents before switching off. Apps with a file open but no I/O yet cannot always be detected; do not open or edit Drive files while it is stopping.
If an upload fails or status cannot be verified, Drive stays mounted. Cancel lets you continue working.

Opening the widget never mounts Drive. Its sign-in shortcut only displays status.
The card, switch, menus and tooltips follow Windows' light/dark app theme automatically, including changes while the widget is open. In Windows' Custom color mode, the app mode determines the widget appearance.
Drag the header to move the card. Its three-dot menu has Hide widget, Keep on top, Status details, Help and Quit widget.
Double-click its tray icon or the desktop shortcut to show it again. Quitting the widget leaves the current mount as it is.
The widget is installed for dev; the existing shared mount permissions are preserved.

GOOGLE DRIVE PLUGIN AND RESEARCH
The ChatGPT Google Drive plugin works while this Windows mount is off.
PDF/LaTeX research can download selected project files to a temporary local workspace, compile and inspect them, then upload and verify the updated source files and final PDFs before removing temporary copies.
This workflow is opt-in: request it explicitly. It has NOT been installed as a global instruction or an automatic cleanup job.
Existing chats rooted in D:\dev\GoogleDrive need the mount, unless you deliberately move their work to another local workspace.
Existing local projects such as D:\dev\Documents\Dynamic Rest Design are unaffected.
No cache or project files are deleted by the widget.

MAINTENANCE
Source project: D:\dev\Documents\seadog\google-drive-widget. Build-Widget.ps1 compiles Widget.cs and MountController.cs to the ignored bin directory. The runtime and installation files remain in D:\dev\Program Files\rclone\widget.
Design previews: GoogleDriveWidget.exe --preview off (follows Windows); add --theme light or --theme dark to preview either palette. Theme overrides apply only in preview mode and never change Windows settings or the mount.
The controller listens only on 127.0.0.1:5579, requires authentication, and is present only while mounted.
The private directory holds a password encrypted for the dev Windows account and an htpasswd hash. Never share these files.
The original task definition and security descriptor are saved beside this README for recovery; they do not contain the stored Windows password.
To restore the original mount startup settings, run Install-Widget.ps1 -Restore as administrator. This does not start the mount immediately. Remove Google Drive Widget.lnk from dev's Startup folder if you also want to stop showing the widget at sign-in.

DEVELOPMENT AND DEPLOYMENT
This repository contains source and setup scripts. Credentials, generated settings, logs, previews and build outputs are not committed. The scripts target this PC's existing dev account and scheduled mount task.

Build from a normal PowerShell session:
  & 'D:\dev\Documents\seadog\google-drive-widget\Build-Widget.ps1'

To update the runnable widget, quit the widget UI first (quitting does not stop Drive), then build directly to its runtime folder:
  & 'D:\dev\Documents\seadog\google-drive-widget\Build-Widget.ps1' -OutputDirectory 'D:\dev\Program Files\rclone\widget'

If setup scripts or this README change, copy the changed files into the runtime folder before using them. Run Prepare-Widget.ps1, Install-Widget.ps1 and Enable-WidgetStartup.ps1 from the runtime folder, not the source checkout: they deliberately use their own directory for settings, credentials, backups and shortcuts.

INSTALL THIS PREPARED COPY
The current runtime executable and protected credentials are already prepared. Save and close open Drive documents. From normal PowerShell as dev, run:
  $install = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File "D:\dev\Program Files\rclone\widget\Install-Widget.ps1"'
  if ($install.ExitCode -ne 0) { throw 'Installation failed. Read D:\dev\Program Files\rclone\widget\installation-status.json for details.' }
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\dev\Program Files\rclone\widget\Enable-WidgetStartup.ps1'
  if ($LASTEXITCODE -ne 0) { throw 'Widget shortcut setup failed.' }
  Start-Process 'D:\dev\Program Files\rclone\widget\GoogleDriveWidget.exe'

Windows prompts for administrator credentials only for the task update. The shortcut step runs as dev. Installation leaves Drive OFF and adds the widget at sign-in. A completed installation still needs an initial ON/OFF check to confirm the stored task credentials work.
