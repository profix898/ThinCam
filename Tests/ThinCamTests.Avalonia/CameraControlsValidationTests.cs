using System.ComponentModel;
using Avalonia.Controls;
using ThinCam.Avalonia;
using static ThinCamTests.Avalonia.VisualTestHelpers;

namespace ThinCamTests.Avalonia;

/// <summary>
/// Verifies that out-of-range input produces an immediate, visible error rather than being
/// silently clamped or only reaching the host application's log.
/// </summary>
[TestClass]
public sealed class CameraControlsValidationTests
{
    /// <summary>
    /// The numeric editors must not carry Minimum/Maximum, because NumericUpDown clamps
    /// silently and that would hide out-of-range entries instead of reporting them.
    /// </summary>
    [TestMethod]
    public void Numeric_editors_do_not_silently_clamp()
        => HeadlessAvalonia.Run(() =>
        {
            var (controls, _) = WithExposureRange();

            var editors = FindAll<NumericUpDown>(controls);
            Assert.AreEqual(2, editors.Count, "Expected the duration and ISO editors.");

            foreach (var editor in editors)
            {
                Assert.AreEqual(Decimal.MinValue, editor.Minimum, "Editor clamps at a minimum.");
                Assert.AreEqual(Decimal.MaxValue, editor.Maximum, "Editor clamps at a maximum.");
            }
        });

    /// <summary>An out-of-range duration must be reported and must never reach the camera.</summary>
    [TestMethod]
    public void Out_of_range_duration_reports_a_validation_error()
        => HeadlessAvalonia.Run(() =>
        {
            var (controls, window) = WithExposureRange();
            var durationEditor = FindAll<NumericUpDown>(controls)[0];

            controls.ManualExposureMilliseconds = 500m;
            Layout(window);

            Assert.AreEqual(500m, controls.ManualExposureMilliseconds, "The value was clamped.");
            Assert.IsTrue(((INotifyDataErrorInfo) controls).HasErrors);
            Assert.IsTrue(controls.HasError);
            Assert.IsTrue(controls.Classes.Contains(":has-error"));
            StringAssert.Contains(controls.LastError, "between 1 and 100");

            Assert.IsTrue(DataValidationErrors.GetHasErrors(durationEditor),
                          "The offending editor shows no field-level error.");
        });

    /// <summary>Correcting the value must clear the banner, the pseudo-class, and the adorner.</summary>
    [TestMethod]
    public void Correcting_the_value_clears_the_error()
        => HeadlessAvalonia.Run(() =>
        {
            var (controls, window) = WithExposureRange();
            var durationEditor = FindAll<NumericUpDown>(controls)[0];

            controls.ManualExposureMilliseconds = 500m;
            Layout(window);
            Assert.IsTrue(controls.HasError);

            controls.ManualExposureMilliseconds = 50m;
            Layout(window);

            Assert.IsFalse(((INotifyDataErrorInfo) controls).HasErrors);
            Assert.IsFalse(controls.HasError);
            Assert.IsFalse(controls.Classes.Contains(":has-error"));
            Assert.IsFalse(DataValidationErrors.GetHasErrors(durationEditor));
        });

    /// <summary>An out-of-range ISO must be reported independently of the duration.</summary>
    [TestMethod]
    public void Out_of_range_iso_reports_a_validation_error()
        => HeadlessAvalonia.Run(() =>
        {
            var (controls, window) = WithExposureRange();
            var isoEditor = FindAll<NumericUpDown>(controls)[1];

            controls.ManualIso = 5000m;
            Layout(window);

            Assert.IsTrue(controls.HasError);
            StringAssert.Contains(controls.LastError, "between 100 and 800");
            Assert.IsTrue(DataValidationErrors.GetHasErrors(isoEditor));
        });

    /// <summary>
    /// Clearing an editor makes <c>NumericUpDown.Value</c> null. Binding that to a
    /// non-nullable property used to throw <see cref="InvalidCastException" />; it must now
    /// fall back to zero, which then fails range validation like any other bad value.
    /// </summary>
    [TestMethod]
    public void Clearing_an_editor_falls_back_to_zero_instead_of_throwing()
        => HeadlessAvalonia.Run(() =>
        {
            var (controls, window) = WithExposureRange();
            var editors = FindAll<NumericUpDown>(controls);
            var durationEditor = editors[0];
            var isoEditor = editors[1];

            durationEditor.Value = 50m;
            isoEditor.Value = 200m;
            Layout(window);
            Assert.IsFalse(controls.HasError, "A valid starting state was expected.");

            durationEditor.Value = null;
            isoEditor.Value = null;
            Layout(window);

            Assert.AreEqual(0m, controls.ManualExposureMilliseconds);
            Assert.AreEqual(0m, controls.ManualIso);
            Assert.IsTrue(DataValidationErrors.GetHasErrors(durationEditor),
                          "An empty editor should read as an out-of-range zero.");
            Assert.IsTrue(controls.HasError);

            durationEditor.Value = 50m;
            isoEditor.Value = 200m;
            Layout(window);

            Assert.IsFalse(DataValidationErrors.GetHasErrors(durationEditor));
            Assert.IsFalse(controls.HasError);
        });

    /// <summary>Builds a control that reports the narrow ranges a real webcam typically exposes.</summary>
    private static (CameraControlsView Controls, Window Window) WithExposureRange()
    {
        var controls = new CameraControlsView();
        var window = Show(controls, 700, 900);

        SetReadOnlyProperty(controls, "HasCamera", true);
        SetReadOnlyProperty(controls, "HasExposureModes", true);
        SetReadOnlyProperty(controls, "IsExposureManual", true);
        SetReadOnlyProperty(controls, "HasManualExposure", true);
        SetReadOnlyProperty(controls, "ManualExposureMinimum", 1m);
        SetReadOnlyProperty(controls, "ManualExposureMaximum", 100m);
        SetReadOnlyProperty(controls, "HasIso", true);
        SetReadOnlyProperty(controls, "IsoMinimum", 100m);
        SetReadOnlyProperty(controls, "IsoMaximum", 800m);

        Layout(window);
        return (controls, window);
    }
}
