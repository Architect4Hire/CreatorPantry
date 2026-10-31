namespace CreatorPantry.Domain.Managers.Ai;

/// <summary>
/// A provider made a picture and would not hand it over (AP.2.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It exists because the money depends on the difference.</strong> A request a provider refuses to run
/// generates nothing and costs nothing; a picture it generated and then withheld on its own safety grounds is
/// a picture it bills for. Both are terminal — asking again buys the same answer — but only one is charged, so
/// the two must be distinguishable at the boundary rather than guessed at.
/// </para>
/// <para>
/// <strong>A type rather than a status code, after getting that wrong once.</strong> Carrying the signal as an
/// HTTP 422 conflated it with the provider's own 422s, which are request rejections: Venice answers
/// <c>CONTENT_POLICY_VIOLATION</c> and <c>IMAGE_TOO_LARGE</c> that way, and charging for those would bill a
/// creator for a picture nobody made. This is declared here, in the shared kernel, because a generator in
/// <c>CreatorPantry.AiProvider</c> can reach it — that assembly references this one — so nothing has to be
/// inferred from a number two sides happen to agree on.
/// </para>
/// <para>
/// It carries no provider message. An image provider's body can quote the prompt back, and a prompt is
/// private creator content (ai.md).
/// </para>
/// </remarks>
public sealed class ImageWithheldException : Exception
{
    public ImageWithheldException()
        : this("The image provider generated a picture and withheld it.")
    {
    }

    public ImageWithheldException(string message)
        : base(message)
    {
    }

    public ImageWithheldException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
