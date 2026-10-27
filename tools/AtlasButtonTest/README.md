# Atlas 8 button test

Interactive, read-only evidence collector for the exact Acer Predator Atlas 8 PA08-I51. It opens
only the recognized 0502:F901 vendor and QAM input collections plus the attributed standard
controller source. It sends no feature/output reports, changes no controller mappings or modes,
creates no virtual controller and does not start the power, lighting or motion services. Existing
Acer software can still respond to its own buttons. The optional `--rear-test` runs only the three
rear-button steps and acquires XInput/Combination mode through the same journaled lifecycle as
the runtime. This option changes controller mode temporarily and verifies restoration on exit.
It writes no profile mappings or ROM. Its recovery journal is retained under
`LocalAppData/LibHandheld/AtlasButtonTest/recovery` if restoration fails.

```powershell
dotnet publish tools/AtlasButtonTest -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/AtlasButtonTest
publish/AtlasButtonTest/LibHandheld.AtlasButtonTest.exe
publish/AtlasButtonTest/LibHandheld.AtlasButtonTest.exe --rear-test
```

Run in an interactive terminal with a keyboard. Exit WSGM or another hardware test first; the tool
refuses an already owned device. Use keyboard Enter to start each step, perform the named action,
release everything, then use Enter to finish. S skips and Q saves a partial run. Ctrl+C also saves.
A recording times out after 60 seconds and the session after 30 minutes. Closing the console
forcibly can lose the current run.

The 27 steps cover neutral input, face buttons, D-pad, shoulders, triggers, stick clicks, View/Menu,
the physical front and rear extras, simultaneous rear presses, stick travel and button rollover.
Left/right mean the user's left/right while holding the device normally, with the screen facing the
user. OEM steps use physical position rather than assuming a label or a decoded mapping. Hold each
ordinary press about one second and leave a second between presses.

Results go to `Desktop/AtlasButtonTest-results` by default; `--out <directory>` overrides it. Share
the resulting JSON. It contains model/firmware facts and collection shapes, per-step changed raw
reports and decoded edges, controller button transitions and axis minima/maxima. It excludes serial
numbers and device paths. Each step retains at most 2048 changes and reports overflow; faults and
missing lanes remain explicit. A completed session is not an automatic pass. Report counts include
duplicate input; the stored raw changes filter consecutive duplicate reports. Controller
stick/trigger samples aggregate extrema without retaining high-rate raw samples.

`--help` and `--self-check` run without opening hardware. The self-check exercises bounded capture,
overflow, per-step reset, duplicate filtering and axis aggregation.

`--front-hold-test` is the focused device-source test: left OEM hold, right OEM hold, Volume Up,
then Volume Down. It is read-only and observes both the low-level shortcut sequence and attributed
Raw Input from keyboard and HID consumer controls. The closed keyboard observation set contains
Win/Alt/Ctrl/Shift, G, Tab, F22 and volume keys. Reports retain scan codes, raw flags and an opaque
device number plus the enumerator/hardware-ID prefix. The instance/serial and interface GUID are
omitted. Repeated keys remain visible. This observes input; it does not suppress it or identify
which user-mode process injected a global keyboard event. OEM services should remain in the same
known state throughout one comparison run.

`--front-tap-test` checks short Guide/QAM presses and volume after the keyboard collection is
disabled. It has four steps and displays Guide/QAM tap counts and consumer-report counts after
each step. Use quick taps with a second between them; holding the button follows a different
firmware path. It is read-only and does not re-enable the keyboard or restart Acer software.
