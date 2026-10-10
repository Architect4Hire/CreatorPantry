using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Tests.Tenancy;

public sealed class SetMeasurementPreferenceViewModelValidatorTests
{
    private readonly SetMeasurementPreferenceViewModelValidator _validator = new();

    [Theory]
    [InlineData(MeasurementSystem.Metric)]
    [InlineData(MeasurementSystem.UsCustomary)]
    public void Metric_and_us_customary_are_valid(MeasurementSystem system) =>
        Assert.True(_validator.Validate(new SetMeasurementPreferenceViewModel(system)).IsValid);

    // Neutral is not a system a recipe can be written in, and B-08 does not offer imperial.
    [Theory]
    [InlineData(MeasurementSystem.Neutral)]
    [InlineData(MeasurementSystem.Imperial)]
    [InlineData((MeasurementSystem)99)]
    public void Any_other_system_is_invalid(MeasurementSystem system) =>
        Assert.False(_validator.Validate(new SetMeasurementPreferenceViewModel(system)).IsValid);

    [Fact]
    public void An_omitted_system_is_a_field_error_rather_than_a_silent_default()
    {
        var result = _validator.Validate(new SetMeasurementPreferenceViewModel(null));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(SetMeasurementPreferenceViewModel.DefaultMeasurementSystem), error.PropertyName);
    }
}
