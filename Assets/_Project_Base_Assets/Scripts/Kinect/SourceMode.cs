// ---------------------------------------------------------------------------------------
// Shared identifier for "which raw Kinect texture do I want." Previously this lived as
// SourceTextureViewer.DisplayMode and every other system (ArucoDetector, etc.) had to reach
// into the viewer to ask for a texture by mode. Pulling it out here lets SourceManager expose
// textures by mode directly, so consumers never need to know the viewer exists.
// ---------------------------------------------------------------------------------------
public enum SourceMode
{
    Depth,
    Infrared,
    Color
}