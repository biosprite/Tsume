# Living with MSI Center M

MSI Center M is the software the Claw ships with. It and Tsume manage the same
settings, so if both are active they undo each other's changes. Tsume never
uninstalls or modifies it.

## While MSI Center M is running

| Area | What happens |
|---|---|
| Performance mode and power limits | MSI Center M may put its own back, for example after sleep. Tsume's helper does not fight it: it restores your choice only while MSI Center M is not running. |
| Charge limit | The same. |
| Lighting, controller mode, back buttons | MSI Center M may overwrite them. |
| The M button | Opens MSI Center M. Tsume ignores it. |
| The Quick Settings button | Opens Game Bar. The firmware does this itself, so it happens with or without MSI Center M. |

## Pausing it

**Pause MSI Center M** in Tsume's Settings stops it without uninstalling
anything. Windows asks for permission each time.

| Step | Pause | Resume |
|---|---|---|
| 1 | Disable its two scheduled tasks, `MSI_Center_M_Server` and `MSI_Center_M_Updater` | Enable both tasks |
| 2 | Stop its running processes | Run the `MSI_Center_M_Server` task |

Uninstalling Tsume resumes it.

To resume it by hand, open **Task Scheduler**, enable both tasks, then
right-click `MSI_Center_M_Server` and choose **Run**.

## The charge limit

MSI Center M remembers its own charge limit and applies it when it starts. So
that it does not undo yours, Tsume also updates that saved setting when you
choose 80 % or 100 %. MSI Center M has no 90 % setting, so at 90 % its saved
value is left as it was.
