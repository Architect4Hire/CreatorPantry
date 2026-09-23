namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// The role a recipe plays in a meal — <c>breakfast</c>, <c>main-course</c>, <c>side</c>, <c>dessert</c>,
/// <c>snack</c>. Global reference data with no <c>WorkspaceId</c> (tenancy.md).
/// </summary>
/// <remarks>
/// <para>
/// One vocabulary covers what the requirements variously call course, meal type, and dish type. They are
/// the same axis asked about in three vocabularies of English, and splitting them into separate tables
/// would double the seed set, the filters, and the pickers to record a distinction nothing currently reads.
/// </para>
/// <para>
/// The consequence, recorded so it is a choice rather than a surprise: a recipe carries one course. Banana
/// bread that is breakfast to one reader and dessert to another is described by the creator's own workspace
/// tags, not by a second course reference here.
/// </para>
/// </remarks>
public class Course : ControlledVocabulary;
