using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using FluentValidation;
using FluentValidation.Validators;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Products;
using Omnichannel.Application.Users;

namespace Omnichannel.Tests.Validation;

/// <summary>
/// Task 012 Part A2: validation messages name fields in the request language (<see cref="FieldNames"/>), not with the
/// technical name FluentValidation derives from the property.
/// </summary>
public sealed class FieldNamesTests
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture; // the default .resx
    private static readonly CultureInfo Vietnamese = new("vi");

    /// <summary>JSON names of every property that has a rule in <paramref name="validator"/>, child rules of collections included.</summary>
    private static IEnumerable<string> ValidatedFields(IValidator validator, Type validatedType)
    {
        foreach (var rule in validator.CreateDescriptor().Rules)
        {
            if (rule.Member is not null)
            {
                yield return ValidationConventions.JsonName(rule.Member.Name);
            }

            foreach (var adaptor in rule.Components.Select(c => c.Validator).OfType<IChildValidatorAdaptor>())
            {
                // RuleForEach(...).ChildRules(...): the element rules live in an inline validator held by the adaptor.
                var context = Activator.CreateInstance(
                    typeof(ValidationContext<>).MakeGenericType(validatedType),
                    RuntimeHelpers.GetUninitializedObject(validatedType))!;
                var child = (IValidator)adaptor.GetType().GetMethod("GetValidator")!.Invoke(adaptor, [context, null])!;
                var elementType = adaptor.GetType().GetGenericArguments()[1];
                foreach (var field in ValidatedFields(child, elementType))
                {
                    yield return field;
                }
            }
        }
    }

    private static Type ValidatedTypeOf(Type validatorType) =>
        validatorType.GetInterfaces()
            .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>))
            .GetGenericArguments()[0];

    [Theory]
    [MemberData(nameof(ValidationMessagesTests.Validators), MemberType = typeof(ValidationMessagesTests))]
    public void Every_validated_field_has_an_english_and_a_vietnamese_name(Type validatorType)
    {
        var validator = (IValidator)Activator.CreateInstance(validatorType)!;

        var fields = ValidatedFields(validator, ValidatedTypeOf(validatorType)).Distinct().ToList();

        Assert.NotEmpty(fields);
        Assert.All(fields, field =>
        {
            Assert.False(string.IsNullOrWhiteSpace(FieldNames.FindExact(field, English)), $"{validatorType.Name}: no English name for '{field}'");
            Assert.False(string.IsNullOrWhiteSpace(FieldNames.FindExact(field, Vietnamese)), $"{validatorType.Name}: no Vietnamese name for '{field}'");
        });
    }

    [Fact]
    public void The_walk_over_validators_reaches_child_rules_and_inherited_rules()
    {
        // Guards the completeness test above against silently checking nothing.
        var import = ValidatedFields(new ImportInventoryRequestValidator(), typeof(ImportInventoryRequest)).ToHashSet();
        Assert.Superset(new HashSet<string> { "documentNumber", "documentSeries", "items", "productId", "stock" }, import);

        var movements = ValidatedFields(new InventoryMovementListQueryValidator(), typeof(InventoryMovementListQuery)).ToHashSet();
        Assert.Superset(new HashSet<string> { "page", "pageSize", "sortBy", "sortDir", "type", "referenceType", "referenceId", "to" }, movements);
    }

    [Fact]
    public void Both_files_define_the_same_names_and_none_is_empty()
    {
        var english = Names(English);
        var vietnamese = Names(Vietnamese);

        Assert.True(english.Count >= 30);
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), vietnamese.Keys.Order(StringComparer.Ordinal));
        Assert.All(english.Concat(vietnamese), name => Assert.False(string.IsNullOrWhiteSpace(name.Value), name.Key));
        // Keys are JSON member names. "from" has no rule of its own; the date-range message names it as the second field.
        Assert.All(english.Keys, key => Assert.Equal(ValidationConventions.JsonName(key), key));
        Assert.Contains("from", english.Keys);
    }

    [Fact]
    public void One_validator_instance_names_fields_in_the_language_of_each_validation()
    {
        // The display name must be resolved when a message is built, not when the rule is declared: validators may be
        // created once and serve requests in both languages.
        var product = new CreateProductRequestValidator();
        var movements = new InventoryMovementListQueryValidator();
        var import = new ImportInventoryRequestValidator();
        var users = new UserListQueryValidator();

        string[] Messages() =>
        [
            product.Validate(new CreateProductRequest("SKU-1", "Name", 1, -1, null)).Errors.Single().ErrorMessage,
            movements.Validate(new InventoryMovementListQuery
            {
                From = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero),
                To = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
            }).Errors.Single().ErrorMessage,
            import.Validate(new ImportInventoryRequest("GRN-1", null, [new ImportInventoryItem(Guid.NewGuid(), 0)])).Errors.Single().ErrorMessage,
            users.Validate(new UserListQuery { Role = "Owner" }).Errors.Single().ErrorMessage
        ];

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var english = Messages();
            CultureInfo.CurrentUICulture = new CultureInfo("vi-VN"); // falls back to "vi"
            var vietnamese = Messages();
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var englishAgain = Messages();

            Assert.StartsWith("'Low-stock threshold' ", english[0]);
            Assert.Equal("'To date' must not be earlier than 'From date'.", english[1]);
            Assert.StartsWith("'Quantity' ", english[2]);
            Assert.Equal("'Role' must be one of: Admin, Manager, Staff.", english[3]);

            Assert.StartsWith("'Ngưỡng cảnh báo tồn kho' ", vietnamese[0]);
            Assert.Equal("'Đến ngày' không được sớm hơn 'Từ ngày'.", vietnamese[1]);
            Assert.StartsWith("'Số lượng' ", vietnamese[2]);
            Assert.Equal("'Vai trò' phải là một trong các vai trò: Admin, Manager, Staff.", vietnamese[3]);

            Assert.Equal(english, englishAgain);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Lookup_follows_the_ui_culture_and_unknown_names_are_programming_errors()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("vi");
            Assert.Equal("Số chứng từ", FieldNames.Get("documentNumber"));
            Assert.Equal("Số chứng từ", FieldNames.ForMember(nameof(ImportInventoryRequest.DocumentNumber)));

            CultureInfo.CurrentUICulture = new CultureInfo("fr"); // not translated: English
            Assert.Equal("Document number", FieldNames.Get("documentNumber"));

            Assert.Null(FieldNames.Find("noSuchField"));
            Assert.Throws<InvalidOperationException>(() => FieldNames.Get("noSuchField"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static Dictionary<string, string> Names(CultureInfo culture)
    {
        var resources = new ResourceManager("Omnichannel.Application.Resources.FieldNames", typeof(FieldNames).Assembly);
        return resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
    }
}
