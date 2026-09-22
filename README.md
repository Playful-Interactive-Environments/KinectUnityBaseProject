# \# Kinect Playground — Base Project

# 

# A Unity base project for building interactive floor/wall installations with a \*\*Kinect v2\*\* depth

# camera. It gives you three things out of the box:

# 

# 1\. \*\*Calibration\*\* — map the Kinect's sensor space onto a real physical floor or wall, and keep

# &#x20;  the render camera lined up with it.

# 2\. \*\*ArUco marker tracking\*\* — detect printed markers in the scene and mirror them as live 3D

# &#x20;  GameObjects (position, rotation, and real-world scale).

# 3\. \*\*Depth blob / hand detection\*\* — find free-standing objects or hands in the depth image and

# &#x20;  turn them into extruded 3D meshes in real time.

# 

# Everything is driven off one shared `Settings` asset and one central frame loop, so the rest of

# this document is mostly about how those pieces talk to each other.

# 

# \---

# 

# \## Prerequisites

# 

# \- \*\*Kinect for Windows SDK v2\*\*, with a Kinect v2 sensor connected — or use `SourceManager`'s

# &#x20; `UseTestImages` mode to develop without hardware (see below).

# \- The project already has native plugins for:

# &#x20; - \*\*ArucoUnity\*\* (OpenCV ArUco bindings) — used by `ArucoDetector`.

# &#x20; - \*\*OpenCvSharp\*\* — used by `BlobObjectDetector` and `PlaygroundMapping`.

# &#x20; - \*\*TextMeshPro\*\* — used by `FPSDisplay`.

# &#x20; - Unity's \*\*new Input System\*\* (`Keyboard.current`) — used by `CalibrationManager` and

# &#x20;   `FPSDisplay`.

# 

# &#x20; These ship with the repo/project — you shouldn't need to install them separately, but if

# &#x20; something doesn't compile, check the `Packages` and `Plugins` folders first.

# \- Kinect + ArucoUnity are Windows-only, so this project targets Windows.

# 

# \---

# 

# \## Getting it running

# 

# 1\. \*\*Start from the calibration scene\*\* — open it directly, or duplicate it as the base for your

# &#x20;  own scene rather than building one from scratch. It already has `SourceManager`, the shared

# &#x20;  `Settings` asset, `CalibrationManager`, `PlaygroundPlane`, and `CameraManager` wired up and

# &#x20;  cross-referenced, which is the fiddly part to get right by hand. Everything else in this guide

# &#x20;  (test images, calibration, markers, blob detection) builds on top of that scene. If you

# &#x20;  duplicate it, double-check the `Settings` asset reference on `SourceManager` carried over

# &#x20;  correctly.

# 2\. \*\*Check the `Settings` asset\*\* (`Assets > Create > ScriptableObjects > Settings` if you need a

# &#x20;  new one). This one asset is wired into almost every script in the scene — if you duplicate it

# &#x20;  or forget to assign it somewhere, that system will silently drift out of sync with the rest.

# 3\. \*\*No Kinect on hand?\*\* Tick `UseTestImages` on `SourceManager` and assign `ColorTestImage` /

# &#x20;  `DepthTestImage` / `InfraTestImage`. The whole pipeline (ArUco, blob detection, calibration)

# &#x20;  runs the same way against static test images. If you \*do\* have a Kinect connected and

# &#x20;  `UseTestImages` is off, `SourceManager` falls back to test images automatically if no sensor

# &#x20;  is found.

# 4\. \*\*Press Play.\*\* `SourceManager` starts pulling frames immediately.

# 5\. \*\*Calibrate the playground:\*\*

# &#x20;  - Hold \*\*Tab\*\* and press \*\*F1\*\* to enter Playground Edit Mode.

# &#x20;  - Drag the magenta center handle to move the rect, the yellow edge handles to resize it.

# &#x20;  - Press \*\*C\*\* to cycle between \*\*Floor\*\* and \*\*Wall\*\* orientation.

# &#x20;  - Press \*\*F1\*\* again (Tab not required this time) to exit — this saves the rect back into the

# &#x20;    `Settings` asset and into `StreamingAssets/settings.json`.

# 

# &#x20;  > \*\*Heads up:\*\* `FPSDisplay`'s default toggle key is also \*\*Tab\*\*. Holding Tab to enter

# &#x20;  > calibration won't fire the FPS overlay, but it's worth knowing both systems use that key.

# 

# \---

# 

# \## How it fits together

# 

# Think of the project as one \*\*event-driven pipeline\*\* rather than a set of scripts polling each

# other every `Update()`:

# 

# ```

# SourceManager (captures Kinect/test frames)

# &#x20;  │

# &#x20;  ├─ OnTexturesInitialized  → fired once, when stream resolution is known

# &#x20;  └─ OnFrameUpdated         → fired every captured frame

# &#x20;       │

# &#x20;       ├─► SourceTextureViewer   (pushes the active mode's texture to a preview material)

# &#x20;       ├─► ArucoDetector         (runs marker detection → OnMarkersUpdated)

# &#x20;       │        └─► MarkerWorldOverlay (syncs a GameObject per detected marker)

# &#x20;       └─► BlobObjectDetector    (thresholds depth → extrudes blobs into meshes)

# ```

# 

# `Settings.OnSettingsChanged` is the other event nearly everything subscribes to — whenever a

# value changes (in the Inspector, via calibration, or loaded from JSON), consumers re-read the

# fields they care about (crop bounds, flip flags, camera framing, ArUco tuning, etc.) instead of

# polling `Settings` every frame.

# 

# \### The pieces, at a glance

# 

# | Script | Role |

# |---|---|

# | `Settings` | Central `ScriptableObject` config — crop/playground/projection rects, flip flags, camera settings, ArUco tuning, frame rate. Loads/saves `StreamingAssets/settings.json`. |

# | `SourceManager` | Owns the Kinect connection (or test images). Runs the main capture loop; exposes raw and "prepared" (cropped + flipped) textures per `SourceMode` (Depth/Infrared/Color). |

# | `PlaygroundMapping` | Static utility — the \*\*only\*\* place crop-rect math, pixel↔world mapping, and flip logic should live. Everything else calls into this instead of reimplementing it. |

# | `PlaygroundPlane` | Represents the calibrated physical surface as a `Transform`, auto-sized from the Kinect resolution. |

# | `OrientationUtility` | Tiny static helper for Floor vs. Wall basis vectors, shared by several components. |

# | `CameraManager` | Positions/orients the render camera to match the calibrated playground rect. |

# | `CalibrationManager` | The interactive drag-to-calibrate tool described above; also draws the Scene-view gizmos (red = detection area, yellow = projection, green/magenta = playground). |

# | `SourceTextureViewer` | Debug/preview material driver — shows the live Color/Depth/Infrared feed, with a depth colour ramp. |

# | `ArucoDetector` | Detects ArUco markers each frame (from Infrared by default, or a webcam for desk-testing) and fills the static `ArucoDetector.Markers` list. |

# | `MarkerObjectRegistry` | `ScriptableObject` mapping marker IDs → prefabs. Shared by `ArucoDetector` and `MarkerWorldOverlay` so they never disagree about which IDs are bound to what. |

# | `MarkerWorldOverlay` | Spawns/updates one GameObject per assigned marker, matching its real-world position, rotation, and scale every frame. |

# | `BlobObjectDetector` | Thresholds the depth image for free-standing objects/hands, cleans up the mask, and extrudes each contour into a 3D mesh. Publishes results via the static `Hands` list. |

# | `BlobObjectDetectorUtilities` | Static geometry helpers (triangulation, contour smoothing, mesh edges) used by `BlobObjectDetector`. |

# | `Structures` | Shared value types: `Marker` (ArUco result) and `Hand` (blob detection result). |

# | `Extensions` | General-purpose static helpers (mesh building, texture creation, popups, quad rotation math) used throughout. |

# | `FPSDisplay` | On-screen FPS/VSync/orientation HUD. \*\*Tab\*\* toggles it, \*\*Escape\*\* quits, \*\*V\*\* toggles VSync. |

# 

# \---

# 

# \## Testing without full hardware

# 

# \- \*\*No Kinect at all:\*\* `SourceManager.UseTestImages` + assign the three test image fields.

# \- \*\*Capture your own test set:\*\* with a Kinect connected and the game running, `SourceManager`'s

# &#x20; custom Inspector has a \*\*"Capture Live Frames as Test Images"\*\* button that saves the current

# &#x20; Color/Depth/Infrared frames into `Assets/KinectTestImages/Capture\_<timestamp>/` and wires them

# &#x20; up as importable test image assets.

# \- \*\*Testing markers without a Kinect:\*\* `ArucoDetector` has a `useWebcamInspector` toggle to run

# &#x20; detection against a regular webcam instead of the Kinect IR stream — handy for tuning marker

# &#x20; detection parameters at your desk.

# 

# \---

# 

# \## Debug views

# 

# \- \*\*`ArucoDetector`\*\* — `showDebugOverlay` shows the exact processed image being fed into OpenCV,

# &#x20; as a quad in the corner of the screen, with rejected marker candidates outlined in red.

# \- \*\*`BlobObjectDetector`\*\* — the `debugStage` dropdown (`Grayscale` / `Threshold` / `Cleaned` /

# &#x20; `Downscaled`) shows any stage of the blob-detection pipeline the same way.

# \- \*\*Scene view gizmos\*\* — `ArucoDetector` draws each detected marker's outline, ID, and size;

# &#x20; `CalibrationManager` draws the detection/projection/playground rects and (in edit mode) the

# &#x20; drag handles.

# 

# \---

# 

# \## A few things worth knowing before you dive in

# 

# \- \*\*One `Settings` asset per setup.\*\* If a script's `Settings` field is empty or points at a

# &#x20; different asset than everyone else's, that script will quietly use stale or default values.

# \- \*\*Flip math lives in `PlaygroundMapping` only.\*\* If you're chasing an orientation/mirroring bug,

# &#x20; that's the one file to fix — every consumer (`ArucoDetector`, `SourceTextureViewer`,

# &#x20; `BlobObjectDetector`) is supposed to call into it rather than flip things themselves.

# \- \*\*`MarkerObjectRegistry` must be the same asset\*\* on both `ArucoDetector` and

# &#x20; `MarkerWorldOverlay`, or marker IDs the detector "assigns" won't resolve to a prefab on the

# &#x20; overlay side.

# \- \*\*In the Editor, the `Settings` asset is authoritative.\*\* `settings.json` gets regenerated from

# &#x20; the asset each session, so an Inspector edit won't be silently overwritten by a stale JSON file.

# &#x20; In a standalone build, the JSON is what persists between runs.# Kinect Playground — Base Project

# 

# A Unity base project for building interactive floor/wall installations with a \*\*Kinect v2\*\* depth

# camera. It gives you three things out of the box:

# 

# 1\. \*\*Calibration\*\* — map the Kinect's sensor space onto a real physical floor or wall, and keep

# &#x20;  the render camera lined up with it.

# 2\. \*\*ArUco marker tracking\*\* — detect printed markers in the scene and mirror them as live 3D

# &#x20;  GameObjects (position, rotation, and real-world scale).

# 3\. \*\*Depth blob / hand detection\*\* — find free-standing objects or hands in the depth image and

# &#x20;  turn them into extruded 3D meshes in real time.

# 

# Everything is driven off one shared `Settings` asset and one central frame loop, so the rest of

# this document is mostly about how those pieces talk to each other.

# 

# \---

# 

# \## Prerequisites

# 

# \- \*\*Kinect for Windows SDK v2\*\*, with a Kinect v2 sensor connected — or use `SourceManager`'s

# &#x20; `UseTestImages` mode to develop without hardware (see below).

# \- The project already has native plugins for:

# &#x20; - \*\*ArucoUnity\*\* (OpenCV ArUco bindings) — used by `ArucoDetector`.

# &#x20; - \*\*OpenCvSharp\*\* — used by `BlobObjectDetector` and `PlaygroundMapping`.

# &#x20; - \*\*TextMeshPro\*\* — used by `FPSDisplay`.

# &#x20; - Unity's \*\*new Input System\*\* (`Keyboard.current`) — used by `CalibrationManager` and

# &#x20;   `FPSDisplay`.

# 

# &#x20; These ship with the repo/project — you shouldn't need to install them separately, but if

# &#x20; something doesn't compile, check the `Packages` and `Plugins` folders first.

# \- Kinect + ArucoUnity are Windows-only, so this project targets Windows.

# 

# \---

# 

# \## Getting it running

# 

# 1\. \*\*Start from the calibration scene\*\* — open it directly, or duplicate it as the base for your

# &#x20;  own scene rather than building one from scratch. It already has `SourceManager`, the shared

# &#x20;  `Settings` asset, `CalibrationManager`, `PlaygroundPlane`, and `CameraManager` wired up and

# &#x20;  cross-referenced, which is the fiddly part to get right by hand. Everything else in this guide

# &#x20;  (test images, calibration, markers, blob detection) builds on top of that scene. If you

# &#x20;  duplicate it, double-check the `Settings` asset reference on `SourceManager` carried over

# &#x20;  correctly.

# 2\. \*\*Check the `Settings` asset\*\* (`Assets > Create > ScriptableObjects > Settings` if you need a

# &#x20;  new one). This one asset is wired into almost every script in the scene — if you duplicate it

# &#x20;  or forget to assign it somewhere, that system will silently drift out of sync with the rest.

# 3\. \*\*No Kinect on hand?\*\* Tick `UseTestImages` on `SourceManager` and assign `ColorTestImage` /

# &#x20;  `DepthTestImage` / `InfraTestImage`. The whole pipeline (ArUco, blob detection, calibration)

# &#x20;  runs the same way against static test images. If you \*do\* have a Kinect connected and

# &#x20;  `UseTestImages` is off, `SourceManager` falls back to test images automatically if no sensor

# &#x20;  is found.

# 4\. \*\*Press Play.\*\* `SourceManager` starts pulling frames immediately.

# 5\. \*\*Calibrate the playground:\*\*

# &#x20;  - Hold \*\*Tab\*\* and press \*\*F1\*\* to enter Playground Edit Mode.

# &#x20;  - Drag the magenta center handle to move the rect, the yellow edge handles to resize it.

# &#x20;  - Press \*\*C\*\* to cycle between \*\*Floor\*\* and \*\*Wall\*\* orientation.

# &#x20;  - Press \*\*F1\*\* again (Tab not required this time) to exit — this saves the rect back into the

# &#x20;    `Settings` asset and into `StreamingAssets/settings.json`.

# 

# &#x20;  > \*\*Heads up:\*\* `FPSDisplay`'s default toggle key is also \*\*Tab\*\*. Holding Tab to enter

# &#x20;  > calibration won't fire the FPS overlay, but it's worth knowing both systems use that key.

# 

# \---

# 

# \## How it fits together

# 

# Think of the project as one \*\*event-driven pipeline\*\* rather than a set of scripts polling each

# other every `Update()`:

# 

# ```

# SourceManager (captures Kinect/test frames)

# &#x20;  │

# &#x20;  ├─ OnTexturesInitialized  → fired once, when stream resolution is known

# &#x20;  └─ OnFrameUpdated         → fired every captured frame

# &#x20;       │

# &#x20;       ├─► SourceTextureViewer   (pushes the active mode's texture to a preview material)

# &#x20;       ├─► ArucoDetector         (runs marker detection → OnMarkersUpdated)

# &#x20;       │        └─► MarkerWorldOverlay (syncs a GameObject per detected marker)

# &#x20;       └─► BlobObjectDetector    (thresholds depth → extrudes blobs into meshes)

# ```

# 

# `Settings.OnSettingsChanged` is the other event nearly everything subscribes to — whenever a

# value changes (in the Inspector, via calibration, or loaded from JSON), consumers re-read the

# fields they care about (crop bounds, flip flags, camera framing, ArUco tuning, etc.) instead of

# polling `Settings` every frame.

# 

# \### The pieces, at a glance

# 

# | Script | Role |

# |---|---|

# | `Settings` | Central `ScriptableObject` config — crop/playground/projection rects, flip flags, camera settings, ArUco tuning, frame rate. Loads/saves `StreamingAssets/settings.json`. |

# | `SourceManager` | Owns the Kinect connection (or test images). Runs the main capture loop; exposes raw and "prepared" (cropped + flipped) textures per `SourceMode` (Depth/Infrared/Color). |

# | `PlaygroundMapping` | Static utility — the \*\*only\*\* place crop-rect math, pixel↔world mapping, and flip logic should live. Everything else calls into this instead of reimplementing it. |

# | `PlaygroundPlane` | Represents the calibrated physical surface as a `Transform`, auto-sized from the Kinect resolution. |

# | `OrientationUtility` | Tiny static helper for Floor vs. Wall basis vectors, shared by several components. |

# | `CameraManager` | Positions/orients the render camera to match the calibrated playground rect. |

# | `CalibrationManager` | The interactive drag-to-calibrate tool described above; also draws the Scene-view gizmos (red = detection area, yellow = projection, green/magenta = playground). |

# | `SourceTextureViewer` | Debug/preview material driver — shows the live Color/Depth/Infrared feed, with a depth colour ramp. |

# | `ArucoDetector` | Detects ArUco markers each frame (from Infrared by default, or a webcam for desk-testing) and fills the static `ArucoDetector.Markers` list. |

# | `MarkerObjectRegistry` | `ScriptableObject` mapping marker IDs → prefabs. Shared by `ArucoDetector` and `MarkerWorldOverlay` so they never disagree about which IDs are bound to what. |

# | `MarkerWorldOverlay` | Spawns/updates one GameObject per assigned marker, matching its real-world position, rotation, and scale every frame. |

# | `BlobObjectDetector` | Thresholds the depth image for free-standing objects/hands, cleans up the mask, and extrudes each contour into a 3D mesh. Publishes results via the static `Hands` list. |

# | `BlobObjectDetectorUtilities` | Static geometry helpers (triangulation, contour smoothing, mesh edges) used by `BlobObjectDetector`. |

# | `Structures` | Shared value types: `Marker` (ArUco result) and `Hand` (blob detection result). |

# | `Extensions` | General-purpose static helpers (mesh building, texture creation, popups, quad rotation math) used throughout. |

# | `FPSDisplay` | On-screen FPS/VSync/orientation HUD. \*\*Tab\*\* toggles it, \*\*Escape\*\* quits, \*\*V\*\* toggles VSync. |

# 

# \---

# 

# \## Testing without full hardware

# 

# \- \*\*No Kinect at all:\*\* `SourceManager.UseTestImages` + assign the three test image fields.

# \- \*\*Capture your own test set:\*\* with a Kinect connected and the game running, `SourceManager`'s

# &#x20; custom Inspector has a \*\*"Capture Live Frames as Test Images"\*\* button that saves the current

# &#x20; Color/Depth/Infrared frames into `Assets/KinectTestImages/Capture\_<timestamp>/` and wires them

# &#x20; up as importable test image assets.

# \- \*\*Testing markers without a Kinect:\*\* `ArucoDetector` has a `useWebcamInspector` toggle to run

# &#x20; detection against a regular webcam instead of the Kinect IR stream — handy for tuning marker

# &#x20; detection parameters at your desk.

# 

# \---

# 

# \## Debug views

# 

# \- \*\*`ArucoDetector`\*\* — `showDebugOverlay` shows the exact processed image being fed into OpenCV,

# &#x20; as a quad in the corner of the screen, with rejected marker candidates outlined in red.

# \- \*\*`BlobObjectDetector`\*\* — the `debugStage` dropdown (`Grayscale` / `Threshold` / `Cleaned` /

# &#x20; `Downscaled`) shows any stage of the blob-detection pipeline the same way.

# \- \*\*Scene view gizmos\*\* — `ArucoDetector` draws each detected marker's outline, ID, and size;

# &#x20; `CalibrationManager` draws the detection/projection/playground rects and (in edit mode) the

# &#x20; drag handles.

# 

# \---

# 

# \## A few things worth knowing before you dive in

# 

# \- \*\*One `Settings` asset per setup.\*\* If a script's `Settings` field is empty or points at a

# &#x20; different asset than everyone else's, that script will quietly use stale or default values.

# \- \*\*Flip math lives in `PlaygroundMapping` only.\*\* If you're chasing an orientation/mirroring bug,

# &#x20; that's the one file to fix — every consumer (`ArucoDetector`, `SourceTextureViewer`,

# &#x20; `BlobObjectDetector`) is supposed to call into it rather than flip things themselves.

# \- \*\*`MarkerObjectRegistry` must be the same asset\*\* on both `ArucoDetector` and

# &#x20; `MarkerWorldOverlay`, or marker IDs the detector "assigns" won't resolve to a prefab on the

# &#x20; overlay side.

# \- \*\*In the Editor, the `Settings` asset is authoritative.\*\* `settings.json` gets regenerated from

# &#x20; the asset each session, so an Inspector edit won't be silently overwritten by a stale JSON file.

# &#x20; In a standalone build, the JSON is what persists between runs.

