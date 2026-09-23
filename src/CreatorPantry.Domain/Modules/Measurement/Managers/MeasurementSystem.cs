using CreatorPantry.Domain.Managers.Reference;
namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>
/// The measurement tradition a unit belongs to. A workspace sets a default display system and a viewer may
/// toggle it for the current view; neither rewrites creator-entered text (B-08).
/// </summary>
/// <remarks>
/// <see cref="UsCustomary"/> and <see cref="Imperial"/> are separate on purpose. They share unit names but
/// not magnitudes — an imperial fluid ounce is about 28.41 ml against the US 29.57 ml, and an imperial pint
/// is 20 fluid ounces against the US 16. Collapsing them into one "imperial-ish" system would silently
/// produce wrong conversions for creators writing in either tradition.
/// </remarks>
public enum MeasurementSystem
{
    /// <summary>Belongs to no system: counts and qualitative units such as "each", "clove", "pinch".</summary>
    Neutral = 0,

    Metric = 1,

    /// <summary>United States customary units, including the US fluid ounce, cup, pint, and Fahrenheit.</summary>
    UsCustomary = 2,

    /// <summary>British imperial units, whose fluid measures differ from their US customary namesakes.</summary>
    Imperial = 3,
}
