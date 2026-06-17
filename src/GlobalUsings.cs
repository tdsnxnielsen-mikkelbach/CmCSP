// Resolve naming conflicts between MudBlazor and ApexCharts that share the same
// unqualified type names. Pages import both namespaces via _Imports.razor; these
// aliases ensure MudBlazor types win for the most common usage (UI colour and
// alignment). ApexCharts types can still be accessed with the full namespace
// (ApexCharts.Color) when needed, though in practice chart colours are set as
// hex strings and ApexCharts.Align is rarely used in C# code.
global using Color = MudBlazor.Color;
global using Align = MudBlazor.Align;
