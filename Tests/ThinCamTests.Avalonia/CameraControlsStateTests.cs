using Avalonia.Controls;
using ThinCam;
using ThinCam.Avalonia;
using static ThinCamTests.Avalonia.VisualTestHelpers;

namespace ThinCamTests.Avalonia;

/// <summary>
/// Verifies the control reflects the camera's reported state and never invents one. Loading
/// capabilities alone must not produce a selection, because the mode lists are ordered by enum
/// value and "first" would be declaration order presented as fact.
/// </summary>
[TestClass]
public sealed class CameraControlsStateTests
{
    /// <summary>Loading capabilities must populate the pickers but select nothing.</summary>
    [TestMethod]
    public void Capabilities_alone_do_not_select_a_mode() =>
        HeadlessAvalonia.Run(() =>
        {
            var controls = new CameraControlsView();
            Show(controls, 700, 900);

            Invoke(controls, "ApplyCapabilities", WebcamCapabilities());

            CollectionAssert.AreEqual(new[] { ExposureMode.Auto, ExposureMode.Manual },
                                      controls.ExposureModes.ToArray());
            CollectionAssert.AreEqual(new[] { FocusMode.Auto, FocusMode.ContinuousAuto, FocusMode.Manual },
                                      controls.FocusModes.ToArray());

            Assert.IsNull(controls.SelectedExposureMode,
                          "Capabilities must not fabricate an exposure mode.");
            Assert.IsNull(controls.SelectedFocusMode,
                          "Capabilities must not fabricate a focus mode; ContinuousAuto was being shown as Auto.");

            Assert.IsFalse(controls.IsExposureManual);
            Assert.IsFalse(controls.IsFocusManual);
        });

    /// <summary>Capability ranges and defaults must still be adopted for the numeric editors.</summary>
    [TestMethod]
    public void Capabilities_still_supply_ranges_and_numeric_defaults() =>
        HeadlessAvalonia.Run(() =>
        {
            var controls = new CameraControlsView();
            Show(controls, 700, 900);

            Invoke(controls, "ApplyCapabilities", WebcamCapabilities());

            Assert.AreEqual(1m, controls.ManualExposureMinimum);
            Assert.AreEqual(100m, controls.ManualExposureMaximum);
            Assert.AreEqual(20m, controls.ManualExposureMilliseconds);
            Assert.AreEqual(100m, controls.IsoMinimum);
            Assert.AreEqual(800m, controls.IsoMaximum);
            Assert.AreEqual(200m, controls.ManualIso);
            Assert.AreEqual(5d, controls.ZoomMaximum, 0.0001);
            Assert.IsTrue(controls.HasZoom);
            Assert.IsFalse(controls.HasLight);
        });

    /// <summary>A mode the camera reports must be shown, including ContinuousAuto.</summary>
    [TestMethod]
    public void Reported_modes_are_reflected_including_continuous_auto() =>
        HeadlessAvalonia.Run(() =>
        {
            var controls = new CameraControlsView();
            var window = Show(controls, 700, 900);

            Invoke(controls, "ApplyCapabilities", WebcamCapabilities());
            SetReadOnlyProperty(controls, "HasCamera", true);

            // Stand in for ReadStateAsync, which assigns the reported modes directly.
            controls.SelectedExposureMode = ExposureMode.Auto;
            controls.SelectedFocusMode = FocusMode.ContinuousAuto;
            Layout(window);

            Assert.AreEqual(ExposureMode.Auto, controls.SelectedExposureMode);
            Assert.AreEqual(FocusMode.ContinuousAuto, controls.SelectedFocusMode);
            Assert.IsFalse(controls.IsFocusManual, "ContinuousAuto is not a manual mode.");

            var pickers = FindAll<ComboBox>(controls);
            Assert.AreEqual(2, pickers.Count);
            Assert.AreEqual(ExposureMode.Auto, pickers[0].SelectedItem);
            Assert.AreEqual(FocusMode.ContinuousAuto, pickers[1].SelectedItem);
        });

    private static CameraCapabilities WebcamCapabilities() =>
        new(new ExposureCapabilities(new HashSet<ExposureMode> { ExposureMode.Auto, ExposureMode.Manual },
                                     new NumericRange<double>(-2, 2, 0, 0.25),
                                     new NumericRange<TimeSpan>(TimeSpan.FromMilliseconds(1),
                                                                TimeSpan.FromMilliseconds(100),
                                                                TimeSpan.FromMilliseconds(20),
                                                                TimeSpan.FromMilliseconds(1)),
                                     new NumericRange<double>(100, 800, 200, 10)),
            new FocusCapabilities(new HashSet<FocusMode> { FocusMode.Auto, FocusMode.ContinuousAuto, FocusMode.Manual },
                                  new NumericRange<double>(0, 1, 0.5, 0.01)),
            new ZoomCapabilities(new NumericRange<double>(1, 5, 1, 0.1)),
            new CameraLightCapabilities(false, false, null));
}
