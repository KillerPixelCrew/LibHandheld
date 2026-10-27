# Atlas 8 button test

Interactive, read-only evidence collector for the exact Acer Predator Atlas 8 PA08-I51. It opens
only the recognized 0502:F901 vendor and QAM input collections plus the attributed standard
controller source. It sends no feature/output reports, changes no controller mappings or modes,
creates no virtual controller and does not start the power, lighting or motion services. Existing
Acer software can still respond to its own buttons.

```powershell
dotnet publish tools/AtlasButtonTest -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/AtlasButtonTest
publish/AtlasButtonTest/LibHandheld.AtlasButtonTest.exe
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
