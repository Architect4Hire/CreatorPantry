using System.Reflection;
using System.Runtime.CompilerServices;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Billing.Integration.Stripe;

namespace CreatorPantry.Tests.Architecture;

/// <summary>
/// No Stripe type outside the gateway (AP.6.1, external.md): the adapter's types stay in its own folder, and what
/// the rest of the platform sees of payment is <see cref="IBillingPaymentGateway"/>, in the application's terms.
/// </summary>
/// <remarks>
/// The adapter talks plain HTTP, so there is no SDK assembly to keep out — the Stripe types are the adapter's own
/// options, settings, metadata names and gateway. Asserted, because the next prompt that needs a Stripe value
/// (AP.6.2's webhook) is exactly where one would be tempted to pass the adapter's settings to a business class.
/// </remarks>
public sealed class StripeBoundaryTests
{
    private const string StripeNamespace = "CreatorPantry.Domain.Modules.Billing.Integration.Stripe";

    private static readonly string[] Hosts =
    [
        "CreatorPantry.Domain",
        "CreatorPantry.ApiService",
        "CreatorPantry.Worker",
        "CreatorPantry.AiProvider",
        "CreatorPantry.Gateway",
        "CreatorPantry.MigrationService",
    ];

    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void Nothing_outside_the_adapter_takes_returns_or_holds_one_of_its_types()
    {
        var holders = Hosts
            .Select(name => Assembly.Load(new AssemblyName(name)))
            .SelectMany(LoadableTypes)
            .Where(type => !InAdapter(Author(type)))
            .Where(Mentions)
            .Select(type => Author(type).FullName!)
            .Distinct()
            .Order()
            .ToList();

        Assert.True(holders.Count == 0, "These name a Stripe adapter type outside the adapter:\n" + string.Join("\n", holders));
    }

    /// <summary>Non-vacuous: the scan sees the adapter's types, so an empty answer above is a real one.</summary>
    [Fact]
    public void The_scan_sees_the_adapter()
    {
        var adapter = LoadableTypes(typeof(StripeBillingRegistration).Assembly).Where(type => type.Namespace == StripeNamespace).ToList();

        Assert.Contains(typeof(StripeBillingRegistration), adapter);
        Assert.Contains(adapter, type => type.Name == "StripeBillingGateway");
        Assert.Contains(adapter, type => type.Name == "StripeBillingSettings");
    }

    /// <summary>The registration is the one way in; everything else in the folder is the adapter's own.</summary>
    [Fact]
    public void Only_the_registration_is_public()
    {
        var exposed = LoadableTypes(typeof(StripeBillingRegistration).Assembly)
            .Where(type => type.Namespace == StripeNamespace && type.IsVisible)
            .ToList();

        Assert.Equal([typeof(StripeBillingRegistration)], exposed);
    }

    /// <summary>
    /// What a caller sees of payment names CreatorPantry's own types and the BCL's: a plan key, a cycle, a redirect —
    /// never a price id, a session object or a Stripe enum.
    /// </summary>
    [Fact]
    public void The_payment_gateway_speaks_only_in_the_applications_terms()
    {
        var gatewayTypes = typeof(IBillingPaymentGateway).GetMethods()
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
            .Concat(typeof(IBillingPaymentGateway).GetProperties().Select(property => property.PropertyType))
            .SelectMany(Unwrap)
            .Distinct()
            .ToList();

        // The gateway's own records, the Billing module's own vocabulary — a reversal's kind (AP.6.3) is the
        // application's word for why money came back, shared with the purchase it is recorded on — and the BCL.
        // Never the adapter's namespace, which the scan above already keeps out of everything else.
        Assert.All(gatewayTypes, type => Assert.True(
            type.Namespace == typeof(IBillingPaymentGateway).Namespace
                || type.Namespace == typeof(BillingPackReversal).Namespace
                || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true,
            $"{type.FullName} is on the payment gateway's surface"));

        // The records it returns carry nothing of the provider's either.
        var records = new[] { typeof(BillingCustomerCreation), typeof(BillingRedirect), typeof(SubscriptionCheckoutRequest), typeof(PackCheckoutRequest), typeof(BillingPortalRequest) };
        Assert.All(
            records.SelectMany(record => record.GetProperties()).Select(property => property.PropertyType).SelectMany(Unwrap),
            type => Assert.False(InAdapter(type), $"{type.FullName} is on a payment record"));
    }

    private static bool InAdapter(Type type) => type.Namespace == StripeNamespace;

    private static bool Mentions(Type type) =>
        type.GetConstructors(Everything).Any(constructor => constructor.GetParameters().Any(parameter => Names(parameter.ParameterType)))
        || type.GetMethods(Everything).Any(method =>
            Names(method.ReturnType) || method.GetParameters().Any(parameter => Names(parameter.ParameterType)))
        || type.GetFields(Everything).Any(field => Names(field.FieldType))
        || type.GetProperties(Everything).Any(property => Names(property.PropertyType));

    private static bool Names(Type type) => Unwrap(type).Any(InAdapter);

    /// <summary>A type and every type argument, element type and generic definition inside it.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var inner in Unwrap(element))
            {
                yield return inner;
            }

            yield break;
        }

        yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
            {
                yield return argument;
            }
        }
    }

    /// <summary>A closure or state machine is charged to the type that wrote it.</summary>
    private static Type Author(Type type)
    {
        var current = type;
        while (current.DeclaringType is { } declaring
            && (current.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || InAdapter(declaring)))
        {
            current = declaring;
        }

        return current;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
