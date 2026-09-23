namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// A surface form resolving to one <see cref="Course"/> — "entrée", "main dish", and "supper" for the main
/// course. The vocabulary where aliases carry the most weight, because imports and generated text name this
/// axis in whatever words their author used.
/// </summary>
/// <inheritdoc cref="VocabularyAlias" path="/remarks"/>
public class CourseAlias : VocabularyAlias;
