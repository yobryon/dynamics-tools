# Predefined X++ global functions

X++ exposes a set of global functions that can be called without a
class context. These are roughly the equivalent of C# extension methods
on `System.String`, `System.Math`, etc. — but in X++ they're top-level
function names you just write directly.

This is the complete list as documented by Microsoft's own Copilot
authoring assets. Function names are case-insensitive in X++ (like all
identifiers); the casing here is the canonical form.

## Conventions

- All function signatures are written `returnType FunctionName(args)`.
- `str` is the X++ string type.
- `anytype` is a late-bound type that holds any value.
- `Date` is X++'s calendar-date type; `utcdatetime` is its UTC
  date+time type.
- `Guid` is X++'s GUID type.
- `Types` is the enum of base X++ types (used by reflection-style calls).

## Category index (quick lookup)

- [Type conversion](#type-conversion-strint-realdate)
- [String operations](#string-operations)
- [Container operations](#container-operations)
- [Date and time](#date-and-time)
- [GUID](#guid)
- [Math](#math)
- [Logging](#logging-info--warning--error)
- [Reflection and types](#reflection-and-types)
- [Labels and globalization](#labels-and-globalization)

## All functions (alphabetical, MS-authored)

| Method Signature | Description |
|-----------------|-------------|
| ` real Abs(real num)` | Returns the absolute value of a real number. |
| ` real Any2Real(anytype obj)` | Converts any object type to real. If the object is numeric, it converts it to real; otherwise, it attempts to parse it as a string. |
| ` str Any2Str(anytype obj)` | Converts anytype value to a string. Returns an empty str if the value is null. |
| ` int Char2Num(str text, int position)` | Converts a character at a specified position in the str to its numeric value (ASCII). |
| ` Type ClassIdGet(object obj)` | Gets the class id/type of the provided object. Returns null if the object is null. |
| ` str Con2Str(container container, str containerElementDelimiter = ",")` | Converts a container (array of objects) to a string, joining the elements using the provided delimiter. |
| ` str Con2StrImplicit(params container container)` | Converts a container to a string, following implicit conversion rules of X++. Returns the first available converted str value. |
| ` container ConDel(container container, int position, int numElements)` | Deletes specified elements from a container starting at a given position. |
| ` int ConFind(container container, object element)` | Finds the index of an element in the container, returning its position (1-based index). |
| ` container ConIns(container container, int start, params container elements)` | Inserts elements into the container at a specified start position. |
| ` int ConLen(container container)` | Returns the length of the container. |
| ` container ConNull()` | Returns an empty (null) container. |
| ` object ConPeek(container container, int index)` | Retrieves an element from the container at the specified index (1-based). |
| ` container ConPoke(container container, int start, params container elements)` | Replaces elements in the container starting from a specified position with new elements. |
| ` str CurrentUserLanguage()` | Gets the current user's language as a string. |
| ` str Date2Str(Date date, str format = null)` | Converts a Date object to a str according to a specified format or the user's culture settings. |
| ` Date DateNull()` | Returns a predefined null date object representing 1st January 1900. |
| ` int DayOfYr(Date d)` | Gets the day of the year for a specified date. |
| ` double DecRound(double value, int realsNum)` | Rounds a double value to the specified number of real places. |
| ` real DecRound(real value, int realsNum)` | Rounds a real value to the specified number of real places. |
| ` Guid EmptyGuid()` | Returns a new empty GUID (`{00000000-0000-0000-0000-000000000000}`). |
| ` str Enum2Str(Enum type)` | Converts an enum to its str representation. |
| ` str Error(str message)` | Writes an error message to the log and returns the message. |
| ` str GetLabel(str labelId, str languageId)` | Retrieves a label in the specified language. |
| ` Date GetNullDate()` | Returns a predefined null date object (1st January 1900). |
| ` utcdatetime GetNullDateTime()` | Returns a null DateTime object set to 1st January 1900 in UTC. |
| ` str Guid2Str(Guid value)` | Converts a Guid to a str representation. |
| ` str Info(str message)` | Writes an informational message to the log and returns the message. |
| ` str Int2Str(int value)` | Converts an integer to its str representation. |
| ` str Int642Str(long value)` | Converts a long integer to its str representation. |
| ` bool IsMatch(str input, str pattern)` | Checks if a given input str matches a specified regex pattern. |
| ` bool IsNullDate(Date d)` | Determines if a date is the null date (1st Jan 1900). |
| ` bool IsNullDateTime(utcdatetime d)` | Determines if a UTC DateTime object is null (1st Jan 1900). |
| ` bool IsNullGuid(Guid g)` | Checks if a given GUID is the empty GUID. |
| ` bool IsSysPackable(object value)` | Checks if the given object implements the SysPackable interface. |
| ` int Match(str pattern, str input)` | Checks if a given input str matches a specified AX-like pattern and returns 1 for a match or 0 for no match. |
| ` real Max(real a, real b)` | Returns the maximum of two values. |
| ` int MaxInt()` | Returns the maximum value for an integer. |
| ` object Min(real a, real b)` | Returns the minimum of two values. |
| ` Date MkDate(int day, int month, int year)` | Creates a Date object based on the specified day, month, and year. |
| ` object NullValueBaseType(Types baseType, bool enumAsInt = false)` | Gets the null value for a specified base type. |
| ` object NullValueFromType(Types baseType)` | Returns the null value corresponding to a given base type. |
| ` str Num2Char(int num)` | Converts an integer to its corresponding character. |
| ` real Power(real value, real powerNum)` | Computes the value raised to the power of the specified exponent. |
| ` int Real2Int(real value)` | Converts a real value to an integer. |
| ` container Str2Con(str text, str containerSeparator)` | Converts a str into a container based on a specified separator. |
| ` container Str2Con_Ru(str value, str separator)` | Converts a str into a container based on a specified separator (Russian version). |
| ` Date Str2Date(str value)` | Converts a str to a Date object. |
| ` bool Str2DateOk(str str)` | Checks if a str can be converted to a Date object. |
| ` utcdatetime Str2DateTime(str value)` | Converts a str to a UTC DateTime object. |
| ` Guid Str2Guid(str str)` | Converts a str to a System.Guid object. |
| ` int Str2Int(str value)` | Converts a str to an integer. |
| ` long Str2Int64(str value)` | Converts a str to a long integer. |
| ` bool Str2Int64Ok(str s)` | Checks if a str can be converted to a long integer. |
| ` bool Str2IntOk(str s)` | Checks if a str can be converted to an integer. |
| ` real Str2Num(str value)` | Converts a str to a real (real) number. |
| ` real Str2Num_Ru(str str)` | Converts a str to a real number (Russian version). |
| ` bool Str2NumOk(str str)` | Validates if a str can be converted to a real number. |
| ` bool Str2NumOk_Ru(str str)` | Validates if a str can be converted to a real number (Russian version). |
| ` int Str2Time(str time)` | Converts a str representation of time to number of seconds. |
| ` int StrCmp(str fromStr, str toStr)` | Compares two strings and returns an integer indicating their relative order. |
| ` bool StrContains(str s1, str s2)` | Checks if one str contains another, ignoring case. |
| ` str StrDel(str str, int start, int len)` | Removes a substr from a string, starting at a specified position for a specified length. |
| ` bool StrEndsWith(str text, str end)` | Checks if a str ends with another string, ignoring case. |
| ` int StrFind(str text, str toFind, int start, int length)` | Finds a substr within a specified part of a string. |
| ` str StrIns(str str, str toInsert, int position)` | Inserts a str at a specified position in another string. |
| ` str StrKeep(str text1, str text2)` | Keeps only the characters in the first str that are present in the second string. |
| ` int StrLen(str str)` | Returns the length of a string. |
| ` str StrLRTrim(str value)` | Trims leading and trailing whitespace from a string. |
| ` str StrLTrim(str value)` | Trims leading whitespace from a string. |
| ` str StrLwr(str text)` | Converts a str to lowercase. |
| ` int StrNFind(str text, str characters, int position, int number)` | Searches for the first occurrence of characters in a substr of a given string. |
| ` str StrPoke(str str, str toStr, int position)` | Overwrites part of a str with another str starting at the specified position. |
| ` str StrRem(str s1, str s2)` | Removes characters in the second str from the first string. |
| ` str StrRep(str str, int rep)` | Repeats a str a specified number of times. |
| ` str StrReplace(str originalString, str fromStr, str toStr)` | Replaces occurrences of one str with another in an original string. |
| ` str StrRTrim(str value)` | Trims trailing whitespace from a string. |
| ` int StrScan(str str, str toFind, int start, int number)` | Finds a substr in a given str starting from a specified position. |
| ` List StrSplit(str originalString, str delimiters)` | Splits a str into a list of substrings based on specified delimiter characters. |
| ` bool StrStartsWith(str text, str start)` | Checks if a str starts with another string, ignoring case. |
| ` str StrUpr(str text)` | Converts a str to uppercase. |
| ` str SubStr(str text, int start, int length)` | Extracts a substr from a given string, starting at a specified position for a required length. |
| ` str SuppressWhiteSpace(str text)` | Removes spaces, line feeds, carriage returns, and tabs from a string. |
| ` Types TypeOf(object o)` | Gets the type of a given object as an enumeration value from the Types enum. |
| ` DateTime UtcDateTime2SystemDateTime(utcdatetime dt)` | Converts a UTC DateTime object to a .NET DateTime object. |
| ` utcdatetime UtcDateTimeNull()` | Returns a null UTC DateTime object set to 1st January 1900. |
| ` str Warning(str message)` | Writes a warning message to the log and returns the message. |

## Notes by category

### Type conversion (str/int/real/Date)

X++'s conversion functions follow a `Source2Target` naming pattern.
The pattern is consistent: `Any2Str`, `Str2Int`, `Str2Date`,
`Int2Str`, `Enum2Str`, `Real2Int`, etc. When a conversion can fail,
there's a paired `*Ok` checker: `Str2IntOk`, `Str2NumOk`,
`Str2DateOk`. Test with `*Ok` before converting if the input is
untrusted.

`Any2Str` and `Any2Real` accept anything (anytype) and try their best;
they're the right tools for late-bound code paths.

### String operations

X++'s string functions are case-INSENSITIVE for comparison helpers
(`StrContains`, `StrStartsWith`, `StrEndsWith`). Use `StrCmp` for
case-sensitive comparison.

Position arguments are 1-based throughout (not 0-based). `StrLen`
returns character count.

`StrSplit` returns a `List` (not a container) — this is unusual for
the X++ standard library; most container-shaped operations use
`container`, not `List`.

### Container operations

Containers are X++'s tuple-of-anything type. The function set:

- Build: `ConNull()`, `[a, b, c]` literal.
- Inspect: `ConLen`, `ConPeek`, `ConFind`.
- Modify (functional — returns new container): `ConIns`, `ConDel`,
  `ConPoke`.
- Interop: `Con2Str` / `Str2Con` for delimiter-based string conversion.

Position arguments are 1-based here too.

### Date and time

- Construction: `MkDate(day, month, year)` (note the day-first order),
  `DateNull()` / `GetNullDate()` for the sentinel (1/1/1900).
- Conversion: `Str2Date` / `Date2Str`, `Str2DateTime`, `UtcDateTimeNull()`.
- Null tests: `IsNullDate`, `IsNullDateTime`.
- Interop: `UtcDateTime2SystemDateTime` to cross into .NET DateTime.

### GUID

`EmptyGuid()` for the all-zeros GUID, `Str2Guid` / `Guid2Str` for
conversion, `IsNullGuid` for the empty-GUID test. There's no `NewGuid()`
in the global function set; use `System.Guid::NewGuid()` from
mscorlib or the framework's `newGuid()` extension method on `Guid`.

### Math

Standard: `Abs`, `Max`, `Min`, `Power`, `DecRound`. Note `Min` returns
`object` (because its arguments are typed `real` but it may need to
return an integer-shaped value); cast as needed.

### Logging (Info / Warning / Error)

`Info(message)`, `Warning(message)`, `Error(message)` write to the
infolog and return the message string. Use them liberally; F&O's
default UI surfaces the infolog automatically and end users expect to
see status there.

`throw Error("...")` is the canonical exception pattern — `Error()`
returns the message and `throw` propagates it. Prefer label references
in the message for translatable text (e.g. `@SYS123456`).

### Reflection and types

- `TypeOf(o)` — runtime type as a `Types` enum value.
- `ClassIdGet(o)` — class ID (the integer that identifies the AOT class).
- `NullValueBaseType` / `NullValueFromType` — the canonical null for a
  base type (useful for late-bound code).
- `IsSysPackable` — checks whether an object can be packed/unpacked
  for cross-tier transit.

### Labels and globalization

- `GetLabel(labelId, languageId)` — resolve a `@SYS` / `@Module:Id`
  label reference programmatically. Most of the time the metadata
  layer does this for you (set a property's value to `@SYS123456` and
  the runtime resolves), but `GetLabel` is the explicit form.
- `CurrentUserLanguage()` — the current user's preferred language as a
  string (e.g. `"en-us"`).
- The `_Ru` variants of `Str2Con`, `Str2Num`, `Str2NumOk` are
  Russia-specific localization functions; you'll rarely need them.
