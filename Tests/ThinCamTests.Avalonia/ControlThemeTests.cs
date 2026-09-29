using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;
using ThinCam.Avalonia;
using static ThinCamTests.Avalonia.VisualTestHelpers;

namespace ThinCamTests.Avalonia;

/// <summary>
/// Verifies that the default control themes are found and applied, so the controls produce a
/// real visual tree instead of silently rendering nothing.
/// </summary>
[TestClass]
public sealed class ControlThemeTests
{
    /// <summary>Starts the shared headless Avalonia thread.</summary>
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext context) => HeadlessAvalonia.Start();

    /// <summary>Stops the shared headless Avalonia thread.</summary>
    [AssemblyCleanup]
    public static void AssemblyCleanup() => HeadlessAvalonia.Stop();

    /// <summary>Verifies the default control theme creates the Skia surface part and template-binds it.</summary>
    [TestMethod]
    public void CameraPreview_applies_the_default_template()
        => HeadlessAvalonia.Run(() =>
        {
            var preview = new CameraPreview();
            Show(preview);

            var surface = Find<CameraPreviewSurface>(preview, "PART_Surface");
            Assert.IsNotNull(surface, "PART_Surface was not created.");
            Assert.AreEqual(preview.Stretch, surface.Stretch);
            Assert.AreEqual(preview.StretchDirection, surface.StretchDirection);
        });

    /// <summary>Verifies placeholder content is presented until a frame is drawn, and honours ShowPlaceholder.</summary>
    [TestMethod]
    public void CameraPreview_shows_the_placeholder_until_a_frame_is_drawn()
        => HeadlessAvalonia.Run(() =>
        {
            var preview = new CameraPreview { PlaceholderContent = "Waiting" };
            Show(preview);

            Assert.IsFalse(preview.HasFrame);
            Assert.IsTrue(preview.IsPlaceholderVisible);

            var placeholder = Find<ContentPresenter>(preview, "PART_Placeholder");
            Assert.IsNotNull(placeholder);
            Assert.AreEqual("Waiting", placeholder.Content);
            Assert.IsTrue(placeholder.IsVisible);

            preview.ShowPlaceholder = false;
            Assert.IsFalse(preview.IsPlaceholderVisible);
        });

    /// <summary>Verifies the control shows only its empty placeholder while no camera is attached.</summary>
    [TestMethod]
    public void CameraControlsView_hides_every_section_until_a_camera_is_set()
        => HeadlessAvalonia.Run(() =>
        {
            var controls = new CameraControlsView();
            Show(controls);

            Assert.IsFalse(controls.HasCamera);
            Assert.IsFalse(controls.Classes.Contains(":has-camera"));

            var sections = Find<StackPanel>(controls, "PART_Sections");
            Assert.IsNotNull(sections, "PART_Sections was not created.");
            Assert.IsFalse(sections.IsVisible);

            var placeholder = Find<TextBlock>(controls, "PART_EmptyPlaceholder");
            Assert.IsNotNull(placeholder);
            Assert.IsTrue(placeholder.IsVisible);
        });

    /// <summary>Verifies the documented section template parts exist so styles can target them.</summary>
    [TestMethod]
    public void CameraControlsView_declares_a_part_for_every_capability_section()
        => HeadlessAvalonia.Run(() =>
        {
            var controls = new CameraControlsView();
            Show(controls);

            foreach (var part in new[] { "PART_ExposureSection", "PART_FocusSection", "PART_ZoomSection", "PART_LightSection" })
                Assert.IsNotNull(Find<Border>(controls, part), $"{part} is missing.");
        });

    /// <summary>Verifies the light and dark theme dictionaries resolve every brush the templates consume.</summary>
    [TestMethod]
    public void Theme_resources_resolve_for_both_theme_variants()
        => HeadlessAvalonia.Run(() =>
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var preview = new CameraPreview();
                var controls = new CameraControlsView();
                var window = new Window { RequestedThemeVariant = variant, Width = 400, Height = 400, Content = new StackPanel { Children = { preview, controls } } };
                window.Show();
                Layout(window);

                Assert.IsNotNull(preview.Background, $"{variant}: preview background.");
                Assert.IsNotNull(preview.Foreground, $"{variant}: preview foreground.");
                Assert.IsNotNull(controls.Foreground, $"{variant}: controls foreground.");

                var section = Find<Border>(controls, "PART_ZoomSection");
                Assert.IsNotNull(section?.Background, $"{variant}: section background.");
                Assert.IsNotNull(section?.BorderBrush, $"{variant}: section border.");
            }
        });

    /// <summary>
    /// Verifies a camera failure sets the control's error state and that a later success clears
    /// it again. Errors are surfaced per control rather than in a banner, so the default
    /// template carries no error element.
    /// </summary>
    [TestMethod]
    public void Failed_operations_set_the_error_state_and_clear_on_success()
        => HeadlessAvalonia.Run(() =>
        {
            var controls = new CameraControlsView();
            Show(controls);

            Assert.IsNull(Find<Border>(controls, "PART_ErrorBanner"),
                          "The default template should not contain an error banner.");
            Assert.IsFalse(controls.HasError);
            Assert.IsNull(controls.LastError);

            RaiseError(controls, new InvalidOperationException("out of range"));

            Assert.IsTrue(controls.HasError);
            Assert.AreEqual("out of range", controls.LastError);
            Assert.IsTrue(controls.Classes.Contains(":has-error"));

            RaiseStatus(controls, "Zoom: 2");

            Assert.IsFalse(controls.HasError);
            Assert.IsFalse(controls.Classes.Contains(":has-error"));
        });

    /// <summary>
    /// Verifies the camera's reported ranges are exposed as properties for validation and for
    /// custom templates.
    /// </summary>
    [TestMethod]
    public void Editor_bounds_are_exposed_as_properties()
        => HeadlessAvalonia.Run(() =>
        {
            var controls = new CameraControlsView();
            Show(controls);

            Assert.AreEqual(0.001m, controls.ManualExposureMinimum);
            Assert.AreEqual(100000m, controls.IsoMaximum);
            Assert.AreEqual(1d, controls.FocusPositionMaximum, 0.0001);
            Assert.AreEqual(0.01d, controls.LightLevelStep, 0.0001);
        });

    private static void RaiseError(CameraControlsView controls, Exception exception) => Invoke(controls, "RaiseError", exception);

    private static void RaiseStatus(CameraControlsView controls, string message) => Invoke(controls, "RaiseStatus", message);
}
