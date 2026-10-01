using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Validators;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Tests.Validation;

/// <summary>
/// Task 011 Part A: every validation text the application writes exists in English and in Vietnamese, through one
/// mechanism (<see cref="ValidationMessages"/>), and no validator carries a text that is the same in both languages.
/// </summary>
public sealed partial class ValidationMessagesTests
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture; // the default .resx
    private static readonly CultureInfo Vietnamese = new("vi");

    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(ValidationMessages.Keys).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            data.Add((string)field.GetRawConstantValue()!);
        }

        return data;
    }

    [Fact]
    public void There_are_message_keys()
    {
        Assert.True(Keys().Count >= 20);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_key_has_an_english_and_a_vietnamese_text_with_the_same_placeholders(string key)
    {
        var english = ValidationMessages.FindExact(key, English);
        var vietnamese = ValidationMessages.FindExact(key, Vietnamese);

        Assert.False(string.IsNullOrWhiteSpace(english), $"{key}: no English text");
        Assert.False(string.IsNullOrWhiteSpace(vietnamese), $"{key}: no Vietnamese text");
        Assert.NotEqual(english, vietnamese);
        Assert.Equal(Placeholders(english!), Placeholders(vietnamese!));
    }

    [Fact]
    public void Text_follows_the_ui_culture_and_placeholders_are_replaced()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("vi-VN"); // falls back to "vi"
            Assert.Equal(
                "'{PropertyName}' chỉ được chứa tối đa 200 dòng.",
                ValidationMessages.Get(ValidationMessages.Keys.ItemsTooMany, ("MaxItems", 200)));

            CultureInfo.CurrentUICulture = new CultureInfo("fr"); // not translated: English
            Assert.Equal(
                "'{PropertyName}' must contain at most 200 items.",
                ValidationMessages.Get(ValidationMessages.Keys.ItemsTooMany, ("MaxItems", 200)));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Unknown_keys_and_unknown_placeholders_are_programming_errors()
    {
        Assert.Throws<InvalidOperationException>(() => ValidationMessages.Get("NoSuchKey"));
        Assert.Throws<InvalidOperationException>(() =>
            ValidationMessages.Get(ValidationMessages.Keys.ItemsTooMany, ("Maximum", 200)));
    }

    public static TheoryData<Type> Validators()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(ValidationMessages).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IValidator).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName))
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void Every_request_validator_is_covered()
    {
        Assert.True(Validators().Count >= 15);
    }

    /// <summary>
    /// Built-in messages are localised by FluentValidation, custom ones by <see cref="ValidationMessages"/>. A message
    /// written as a literal (<c>.WithMessage("...")</c>) is identical in both languages and fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(Validators))]
    public void No_rule_has_the_same_message_in_english_and_vietnamese(Type validatorType)
    {
        var validator = (IValidator)Activator.CreateInstance(validatorType)!;

        var english = MessagesIn(validator, new CultureInfo("en"));
        var vietnamese = MessagesIn(validator, Vietnamese);

        Assert.NotEmpty(english);
        Assert.Equal(english.Count, vietnamese.Count);
        Assert.All(english.Zip(vietnamese), pair =>
            Assert.False(pair.First.Message == pair.Second.Message, $"{pair.First.Rule}: \"{pair.First.Message}\" is not translated"));
    }

    private static List<(string Rule, string Message)> MessagesIn(IValidator validator, CultureInfo culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = culture;
            return validator.CreateDescriptor().Rules
                .SelectMany(rule => rule.Components
                    // Child validators (RuleForEach(...).ChildRules) have no message of their own.
                    .Where(component => component.Validator is not IChildValidatorAdaptor)
                    .Select(component =>
                        (Rule: $"{rule.PropertyName}/{component.Validator.Name}", Message: component.GetUnformattedErrorMessage())))
                .ToList();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static string[] Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(match => match.Value).Order().ToArray();

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex PlaceholderPattern();
}
