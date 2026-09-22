# Natural-Language Query Safety

Translate natural language into a constrained, typed filter—not SQL, LINQ text, or arbitrary expressions.

```csharp
public enum RecipeSearchField
{
    Title,
    Cuisine,
    Status,
    UpdatedAt,
    PrepMinutes,
    TotalMinutes
}

public enum ComparisonOperator
{
    Equals,
    Contains,
    GreaterThan,
    LessThan,
    OnOrAfter,
    OnOrBefore
}

public sealed record FilterClause(
    RecipeSearchField Field,
    ComparisonOperator Operator,
    string Value);

public sealed record RecipeFilter(
    IReadOnlyList<FilterClause> Clauses,
    RecipeSearchField? SortBy,
    bool Descending,
    int Limit);
```

Server code validates field/operator compatibility, parses values using invariant rules, caps clauses and limit, applies workspace scope before filters, and translates the object through a whitelist. Values remain parameterized data. Display the interpreted filters to the creator and allow correction.

Write commands use a separate schema and always follow preview → confirm → deterministic execution. A generated filter can search; it cannot mutate.

Tests cover unknown fields, invalid operators, malformed values, injection strings treated as literals, excessive limits, workspace isolation, and ambiguous language.

