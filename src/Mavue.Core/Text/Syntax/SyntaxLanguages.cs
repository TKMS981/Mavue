namespace Mavue.Core.Text.Syntax;

/// <summary>
/// The languages Quick View colors, chosen by file name or extension (docs/ARCHITECTURE.md §4.7.12). Plain text
/// (.txt, .csv…) has no language. Definitions are created on first use.
/// </summary>
public static class SyntaxLanguages
{
    private const string CKeywords = "auto break case char const continue default do double else enum extern float for goto if inline int long register restrict return short signed sizeof static struct switch typedef union unsigned void volatile while _Bool _Complex _Atomic _Generic _Noreturn _Static_assert _Thread_local";
    private const string CppKeywords = CKeywords + " alignas alignof and asm bool catch class co_await co_return co_yield concept consteval constexpr constinit const_cast decltype delete dynamic_cast explicit export false friend mutable namespace new noexcept not nullptr operator or override final private protected public reinterpret_cast requires static_assert static_cast template this thread_local throw true try typeid typename using virtual wchar_t char8_t char16_t char32_t";
    private const string CTypes = "int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t size_t ssize_t ptrdiff_t intptr_t uintptr_t wchar_t FILE BOOL BYTE WORD DWORD QWORD HANDLE HRESULT LPCWSTR LPWSTR LPCSTR LPSTR UINT ULONG LONG HWND std string vector map";
    private const string CSharpKeywords = "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw try typeof uint ulong unchecked unsafe ushort using virtual void volatile while add alias and ascending async await by descending dynamic equals file from get global group init into join let managed nameof nint not notnull nuint on or orderby partial record remove required scoped select set unmanaged value var when where with yield";
    private const string JavaKeywords = "abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for goto if implements import instanceof int interface long native new package private protected public return short static strictfp super switch synchronized this throw throws transient try void volatile while var record sealed permits non-sealed yield module requires exports opens uses provides";
    private const string JsKeywords = "async await break case catch class const continue debugger default delete do else export extends finally for from function get if import in instanceof let new of return set static super switch this throw try typeof var void while with yield";
    private const string TsKeywords = JsKeywords + " abstract any as asserts bigint boolean declare enum implements infer interface is keyof module namespace never number object override private protected public readonly require satisfies string symbol type unique unknown accessor";
    private const string PythonKeywords = "and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield match case type self cls print";
    private const string GoKeywords = "break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var";
    private const string GoTypes = "bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any comparable";
    private const string RustKeywords = "as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait type union unsafe use where while macro_rules";
    private const string RustTypes = "bool char f32 f64 i8 i16 i32 i64 i128 isize str u8 u16 u32 u64 u128 usize String Vec Option Result Box";
    private const string KotlinKeywords = "abstract actual annotation as break by catch class companion const constructor continue crossinline data do dynamic else enum expect external final finally for fun get if import in infix init inline inner interface internal is lateinit noinline object open operator out override package private protected public reified return sealed set super suspend tailrec this throw try typealias typeof val var vararg when where while";
    private const string SwiftKeywords = "actor any as associatedtype async await break case catch class continue default defer deinit do else enum extension fallthrough fileprivate for func guard if import in init inout internal is let nonisolated open operator private protocol public repeat rethrows return self Self some static struct subscript super switch throw throws try typealias var where while";
    private const string PhpKeywords = "abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty enddeclare endfor endforeach endif endswitch endwhile enum extends final finally fn for foreach function global goto if implements include include_once instanceof insteadof interface isset list match namespace new or print private protected public readonly require require_once return static switch throw trait try unset use var while xor yield";
    private const string RubyKeywords = "BEGIN END alias and begin break case class def defined? do else elsif end ensure for if in module next not or redo rescue retry return self super then undef unless until when while yield require require_relative attr_accessor attr_reader attr_writer private protected public include extend raise puts lambda proc";
    private const string SqlKeywords = "add all alter and any as asc authorization backup begin between break browse bulk by cascade case check checkpoint close clustered coalesce collate column commit compute constraint contains continue convert create cross current current_date current_time current_timestamp current_user cursor database deallocate declare default delete deny desc distinct distributed double drop else end errlvl escape except exec execute exists exit external fetch file fillfactor for foreign freetext from full function goto grant group having holdlock identity if in index inner insert intersect into is join key kill left like limit lineno load merge national nocheck nonclustered not nullif of off offsets on open option or order outer over percent pivot plan precision primary print proc procedure public raiserror read readtext reconfigure references replication restore restrict return revert revoke right rollback rowcount rowguidcol rule save schema select session_user set setuser shutdown some statistics system_user table tablesample textsize then to top tran transaction trigger truncate try_convert union unique unpivot update updatetext use user values varying view waitfor when where while with within writetext returning window partition rows range offset fetch next only lateral recursive explain analyze vacuum pragma";
    private const string SqlTypes = "bigint binary bit blob boolean char date datetime datetime2 decimal float image int integer money nchar ntext numeric nvarchar real smallint text time timestamp tinyint uniqueidentifier varbinary varchar xml json jsonb serial uuid";
    private const string ShellKeywords = "if then else elif fi case esac for select while until do done in function time coproc return exit break continue local export readonly declare typeset unset shift source alias echo printf read cd test trap eval exec set";
    private const string PowerShellKeywords = "begin break catch class clean continue data define do dynamicparam else elseif end enum exit filter finally for foreach from function hidden if in param process return static switch throw trap try until using var while workflow parallel sequence inlinescript -and -or -not -xor -band -bor -eq -ne -gt -ge -lt -le -like -notlike -match -notmatch -contains -notcontains -in -notin -replace -split -join -is -isnot -as";
    private const string BatchKeywords = "if else for in do goto call exit set setlocal endlocal echo off on not exist defined errorlevel equ neq lss leq gtr geq shift pause start cd pushd popd md mkdir rd rmdir del copy move ren type cls title choice where findstr";
    private const string LuaKeywords = "and break do else elseif end for function goto if in local not or repeat return then until while";
    private const string DartKeywords = "abstract as assert async await base break case catch class const continue covariant default deferred do dynamic else enum export extends extension external factory final finally for Function get hide if implements import in interface is late library mixin new on operator part required rethrow return sealed set show static super switch sync this throw try typedef var void when while with yield";
    private const string ScalaKeywords = "abstract case catch class def do else enum export extends final finally for forSome given if implicit import lazy match new object override package private protected return sealed super then this throw trait try type using val var while with yield";
    private const string VbKeywords = "AddHandler AddressOf Alias And AndAlso As Boolean ByRef Byte ByVal Call Case Catch CBool CByte CChar CDate CDbl CDec Char CInt Class CLng CObj Const Continue CSByte CShort CSng CStr CType CUInt CULng CUShort Date Decimal Declare Default Delegate Dim DirectCast Do Double Each Else ElseIf End EndIf Enum Erase Error Event Exit Finally For Friend Function Get GetType Global GoSub GoTo Handles If Implements Imports In Inherits Integer Interface Is IsNot Let Lib Like Long Loop Me Mod Module MustInherit MustOverride MyBase MyClass Namespace Narrowing New Next Not Object Of On Operator Option Optional Or OrElse Overloads Overridable Overrides ParamArray Partial Private Property Protected Public RaiseEvent ReadOnly ReDim RemoveHandler Resume Return SByte Select Set Shadows Shared Short Single Static Step Stop String Structure Sub SyncLock Then Throw To Try TryCast TypeOf UInteger ULong UShort Using Variant Wend When While Widening With WithEvents WriteOnly Xor";
    private const string FSharpKeywords = "abstract and as assert base begin class default delegate do done downcast downto elif else end exception extern false finally fixed for fun function global if in inherit inline interface internal lazy let match member module mutable namespace new not null of open or override private public rec return select sig static struct then to true try type upcast use val void when while with yield async task";
    private const string ShaderKeywords = CKeywords + " cbuffer tbuffer struct in out inout uniform varying attribute layout precision highp mediump lowp discard technique pass sampler sampler2D sampler3D samplerCube texture Texture2D Texture3D TextureCube SamplerState RWTexture2D StructuredBuffer RWStructuredBuffer groupshared numthreads register packoffset";
    private const string ShaderTypes = "bool int uint float double half bool2 bool3 bool4 int2 int3 int4 uint2 uint3 uint4 float2 float3 float4 half2 half3 half4 float2x2 float3x3 float4x4 vec2 vec3 vec4 ivec2 ivec3 ivec4 uvec2 uvec3 uvec4 bvec2 bvec3 bvec4 mat2 mat3 mat4 mat2x2 mat3x3 mat4x4";
    private const string PerlKeywords = "my our local sub package use require no if elsif else unless while until for foreach do last next redo return print printf say die warn eval defined undef exists delete shift unshift push pop splice keys values each scalar ref bless qw qq";
    private const string RKeywords = "if else repeat while function for in next break library require return switch";
    private const string HaskellKeywords = "case class data default deriving do else foreign if import in infix infixl infixr instance let module newtype of then type where";
    private const string ElixirKeywords = "after and catch cond def defmacro defmodule defp defprotocol defimpl defstruct do else end fn for if import in not or quote raise receive require rescue try unless unquote use when with";
    private const string ProtoKeywords = "syntax package import option message enum service rpc returns stream repeated optional required oneof map reserved extensions extend to max public weak edition";
    private const string ProtoTypes = "double float int32 int64 uint32 uint64 sint32 sint64 fixed32 fixed64 sfixed32 sfixed64 bool string bytes";
    private const string GraphQlKeywords = "query mutation subscription fragment on type interface union enum input scalar schema extend directive implements repeatable";
    private const string HclKeywords = "resource data variable output locals module provider terraform for in if else for_each count depends_on dynamic content";
    private const string ZigKeywords = "addrspace align allowzero and anyframe anytype asm async await break callconv catch comptime const continue defer else enum errdefer error export extern fn for if inline linksection noalias noinline nosuspend opaque or orelse packed pub resume return struct suspend switch test threadlocal try union unreachable usingnamespace var volatile while";
    private const string DockerKeywords = "FROM AS RUN CMD LABEL MAINTAINER EXPOSE ENV ADD COPY ENTRYPOINT VOLUME USER WORKDIR ARG ONBUILD STOPSIGNAL HEALTHCHECK SHELL";
    private const string MakeKeywords = "ifeq ifneq ifdef ifndef else endif include sinclude override export unexport define endef vpath";
    private const string CMakeKeywords = "if elseif else endif foreach endforeach while endwhile function endfunction macro endmacro return set unset option project cmake_minimum_required add_executable add_library target_link_libraries target_include_directories target_compile_definitions target_compile_options find_package include add_subdirectory message install";
    private const string LatexKeywords = "";
    private const string AsmKeywords = "mov lea push pop call ret jmp je jne jz jnz jg jge jl jle ja jae jb jbe cmp test add sub mul imul div idiv inc dec and or xor not shl shr sar sal nop int syscall section global extern db dw dd dq resb resw resd resq equ times proc endp segment ends";
    private const string LispKeywords = "defun defmacro defvar defparameter defconst define lambda let let* letrec if cond when unless case and or not progn begin quote setq setf loop do require provide ns fn def defn defn- loop recur";

    private static readonly Dictionary<string, Func<SyntaxLanguage>> ByExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Func<SyntaxLanguage>> ByFileName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Func<SyntaxLanguage>, SyntaxLanguage> Created = [];
    private static readonly Lock Gate = new();

    static SyntaxLanguages()
    {
        static StringForm Dq(bool multiline = false) => new("\"", "\"", '\\', multiline);
        static StringForm Sq(bool multiline = false) => new("'", "'", '\\', multiline);

        Map(() => new CodeLanguage("c", "C", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [Dq(), Sq()], Keywords = CKeywords, Types = CTypes,
            Literals = "true false NULL nullptr", HashPreprocessor = true,
        }), ".c", ".h");
        Map(() => new CodeLanguage("cpp", "C++", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("R\"(", ")\"", '\0', true), Dq(), Sq()], Keywords = CppKeywords, Types = CTypes,
            Literals = "true false NULL nullptr", HashPreprocessor = true, StringPrefixes = "LuU8",
        }), ".cpp", ".cc", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".h++", ".inl", ".ipp", ".tpp", ".ixx", ".cppm", ".cu", ".cuh", ".m", ".mm", ".ino");
        Map(() => new CodeLanguage("csharp", "C#", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")],
            Strings = [new("\"\"\"", "\"\"\"", '\0', true), new("@\"", "\"", '\0', true, true), Dq(), Sq()],
            Keywords = CSharpKeywords, HashPreprocessor = true, CapitalizedTypes = true, StringPrefixes = "$@u8",
        }), ".cs", ".csx", ".cake");
        Map(() => new CodeLanguage("java", "Java", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("\"\"\"", "\"\"\"", '\\', true), Dq(), Sq()],
            Keywords = JavaKeywords, Decorators = true, CapitalizedTypes = true,
        }), ".java", ".jav");
        Map(() => new CodeLanguage("kotlin", "Kotlin", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("\"\"\"", "\"\"\"", '\0', true), Dq(), Sq()],
            Keywords = KotlinKeywords, Decorators = true, CapitalizedTypes = true, VariablePrefixes = "$",
        }), ".kt", ".kts");
        Map(() => new CodeLanguage("scala", "Scala", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("\"\"\"", "\"\"\"", '\0', true), Dq(), Sq()],
            Keywords = ScalaKeywords, Decorators = true, CapitalizedTypes = true, StringPrefixes = "sfr",
        }), ".scala", ".sc", ".sbt");
        Map(() => new CodeLanguage("groovy", "Groovy", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("\"\"\"", "\"\"\"", '\\', true), new("'''", "'''", '\\', true), Dq(), Sq()],
            Keywords = JavaKeywords + " def in as trait", Decorators = true, CapitalizedTypes = true, VariablePrefixes = "$",
        }), ".groovy", ".gradle", ".gvy");
        Map(() => new CodeLanguage("javascript", "JavaScript", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("`", "`", '\\', true), Dq(), Sq()],
            Keywords = JsKeywords, Literals = "true false null undefined NaN Infinity", Decorators = true, CapitalizedTypes = true,
        }), ".js", ".mjs", ".cjs", ".jsx", ".es6", ".jsm");
        Map(() => new CodeLanguage("typescript", "TypeScript", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("`", "`", '\\', true), Dq(), Sq()],
            Keywords = TsKeywords, Literals = "true false null undefined NaN Infinity", Decorators = true, CapitalizedTypes = true,
        }), ".ts", ".mts", ".cts", ".tsx");
        Map(() => new CodeLanguage("json", "JSON", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [Dq(), Sq()], KeysBeforeColon = true,
        }), ".json", ".jsonc", ".json5", ".geojson", ".webmanifest", ".har", ".ipynb", ".babelrc", ".eslintrc", ".prettierrc", ".jsonl", ".ndjson", ".code-workspace", ".resjson", ".topojson");
        Map(() => new CodeLanguage("python", "Python", new CodeLanguageDefinition
        {
            LineComments = ["#"], Strings = [new("\"\"\"", "\"\"\"", '\\', true), new("'''", "'''", '\\', true), Dq(), Sq()],
            Keywords = PythonKeywords, Literals = "True False None", Decorators = true, StringPrefixes = "rRbBfFuUtT",
        }), ".py", ".pyw", ".pyi", ".pyx", ".pxd", ".gyp", ".gypi", ".bzl", ".star");
        Map(() => new CodeLanguage("go", "Go", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("`", "`", '\0', true), Dq(), Sq()],
            Keywords = GoKeywords, Types = GoTypes, Literals = "true false nil iota", CapitalizedTypes = true,
        }), ".go");
        Map(() => new CodeLanguage("rust", "Rust", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("r#\"", "\"#", '\0', true), new("r\"", "\"", '\0', true), Dq(true)],
            Keywords = RustKeywords, Types = RustTypes, Literals = "true false None Some Ok Err", CapitalizedTypes = true, Decorators = false,
        }), ".rs");
        Map(() => new CodeLanguage("swift", "Swift", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("\"\"\"", "\"\"\"", '\\', true), Dq()],
            Keywords = SwiftKeywords, Literals = "true false nil", Decorators = true, CapitalizedTypes = true,
        }), ".swift");
        Map(() => new CodeLanguage("dart", "Dart", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [new("'''", "'''", '\\', true), new("\"\"\"", "\"\"\"", '\\', true), Dq(), Sq()],
            Keywords = DartKeywords, Decorators = true, CapitalizedTypes = true, VariablePrefixes = "$",
        }), ".dart");
        Map(() => new CodeLanguage("php", "PHP", new CodeLanguageDefinition
        {
            LineComments = ["//", "#"], BlockComments = [("/*", "*/")], Strings = [Dq(true), Sq(true)],
            Keywords = PhpKeywords, Literals = "true false null TRUE FALSE NULL", VariablePrefixes = "$", CaseInsensitive = false, CapitalizedTypes = true,
        }), ".php", ".phtml", ".php3", ".php4", ".php5", ".phps");
        Map(() => new CodeLanguage("ruby", "Ruby", new CodeLanguageDefinition
        {
            LineComments = ["#"], BlockComments = [("=begin", "=end")], Strings = [Dq(), Sq()],
            Keywords = RubyKeywords, Literals = "true false nil", CapitalizedTypes = true, WordCharacters = "?!", VariablePrefixes = "@$",
        }), ".rb", ".rake", ".gemspec", ".ru", ".erb", ".rbw");
        Map(() => new CodeLanguage("perl", "Perl", new CodeLanguageDefinition
        {
            LineComments = ["#"], Strings = [Dq(), Sq()], Keywords = PerlKeywords, VariablePrefixes = "$@%",
        }), ".pl", ".pm", ".t", ".pod");
        Map(() => new CodeLanguage("lua", "Lua", new CodeLanguageDefinition
        {
            LineComments = ["--"], BlockComments = [("--[[", "]]")], Strings = [new("[[", "]]", '\0', true), Dq(), Sq()],
            Keywords = LuaKeywords, Literals = "true false nil",
        }), ".lua", ".luau");
        Map(() => new CodeLanguage("r", "R", new CodeLanguageDefinition
        {
            LineComments = ["#"], Strings = [Dq(true), Sq(true)], Keywords = RKeywords, Literals = "TRUE FALSE NULL NA NaN Inf T F",
        }), ".r", ".rmd");
        Map(() => new CodeLanguage("julia", "Julia", new CodeLanguageDefinition
        {
            LineComments = ["#"], BlockComments = [("#=", "=#")], Strings = [new("\"\"\"", "\"\"\"", '\\', true), Dq()],
            Keywords = "abstract baremodule begin break catch const continue do else elseif end export finally for function global if import let local macro module mutable quote return struct try using while where", Literals = "true false nothing missing", CapitalizedTypes = true, Decorators = true,
        }), ".jl");
        Map(() => new CodeLanguage("haskell", "Haskell", new CodeLanguageDefinition
        {
            LineComments = ["--"], BlockComments = [("{-", "-}")], Strings = [Dq()], Keywords = HaskellKeywords, Literals = "True False Nothing Just", CapitalizedTypes = true,
        }), ".hs", ".lhs");
        Map(() => new CodeLanguage("fsharp", "F#", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("(*", "*)")], Strings = [new("\"\"\"", "\"\"\"", '\0', true), new("@\"", "\"", '\0', true, true), Dq()],
            Keywords = FSharpKeywords, CapitalizedTypes = true, HashPreprocessor = true, StringPrefixes = "$",
        }), ".fs", ".fsx", ".fsi", ".ml", ".mli");
        Map(() => new CodeLanguage("elixir", "Elixir", new CodeLanguageDefinition
        {
            LineComments = ["#"], Strings = [new("\"\"\"", "\"\"\"", '\\', true), Dq(), Sq()], Keywords = ElixirKeywords, Literals = "true false nil", CapitalizedTypes = true, WordCharacters = "?!",
        }), ".ex", ".exs", ".eex", ".heex");
        Map(() => new CodeLanguage("erlang", "Erlang", new CodeLanguageDefinition
        {
            LineComments = ["%"], Strings = [Dq()], Keywords = "after and andalso band begin bnot bor bsl bsr bxor case catch cond div end fun if let not of or orelse receive rem try when xor", Literals = "true false undefined",
        }), ".erl", ".hrl");
        Map(() => new CodeLanguage("lisp", "Lisp", new CodeLanguageDefinition
        {
            LineComments = [";"], Strings = [Dq(true)], Keywords = LispKeywords, Literals = "t nil true false", WordCharacters = "-*?!+<>=/",
        }), ".lisp", ".lsp", ".el", ".scm", ".ss", ".rkt", ".clj", ".cljs", ".cljc", ".edn", ".fnl");
        Map(() => new CodeLanguage("zig", "Zig", new CodeLanguageDefinition
        {
            LineComments = ["//"], Strings = [Dq(), Sq()], Keywords = ZigKeywords, Literals = "true false null undefined", CapitalizedTypes = true,
        }), ".zig", ".zon");
        Map(() => new CodeLanguage("sql", "SQL", new CodeLanguageDefinition
        {
            LineComments = ["--", "#"], BlockComments = [("/*", "*/")], Strings = [new("'", "'", '\0', true, true), new("\"", "\"", '\0', false, true), new("`", "`", '\0')],
            Keywords = SqlKeywords, Types = SqlTypes, Literals = "true false null", CaseInsensitive = true,
        }), ".sql", ".ddl", ".dml", ".pgsql", ".psql", ".mysql", ".sqlite", ".hql", ".kql", ".cql");
        Map(() => new CodeLanguage("graphql", "GraphQL", new CodeLanguageDefinition
        {
            LineComments = ["#"], Strings = [new("\"\"\"", "\"\"\"", '\0', true), Dq()], Keywords = GraphQlKeywords, CapitalizedTypes = true, VariablePrefixes = "$", Decorators = true,
        }), ".graphql", ".gql", ".graphqls");
        Map(() => new CodeLanguage("protobuf", "Protocol Buffers", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [Dq(), Sq()], Keywords = ProtoKeywords, Types = ProtoTypes, CapitalizedTypes = true,
        }), ".proto");
        Map(() => new CodeLanguage("hcl", "HCL", new CodeLanguageDefinition
        {
            LineComments = ["#", "//"], BlockComments = [("/*", "*/")], Strings = [new("<<EOF", "EOF", '\0', true), Dq()], Keywords = HclKeywords, VariablePrefixes = "$",
        }), ".tf", ".tfvars", ".hcl", ".nomad");
        Map(() => new CodeLanguage("nix", "Nix", new CodeLanguageDefinition
        {
            LineComments = ["#"], BlockComments = [("/*", "*/")], Strings = [new("''", "''", '\0', true), Dq(true)],
            Keywords = "assert else if in inherit let or rec then with import", VariablePrefixes = "$",
        }), ".nix");
        Map(() => new CodeLanguage("shader", "Shader", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [Dq()], Keywords = ShaderKeywords, Types = ShaderTypes, HashPreprocessor = true, Literals = "true false",
        }), ".hlsl", ".hlsli", ".fx", ".fxh", ".glsl", ".vert", ".frag", ".geom", ".tesc", ".tese", ".comp", ".shader", ".cginc", ".compute", ".usf", ".ush", ".metal", ".wgsl");
        Map(() => new CodeLanguage("solidity", "Solidity", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("/*", "*/")], Strings = [Dq(), Sq()],
            Keywords = "pragma solidity contract interface library function modifier event struct enum mapping public private internal external pure view payable returns return memory storage calldata if else for while do break continue emit require revert assert import is using constructor override virtual abstract", Types = "address bool string bytes uint int uint256 int256 bytes32 uint8",
        }), ".sol");
        Map(() => new CodeLanguage("vb", "Visual Basic", new CodeLanguageDefinition
        {
            LineComments = ["'", "REM "], Strings = [new("\"", "\"", '\0', false, true)], Keywords = VbKeywords, Literals = "True False Nothing", CaseInsensitive = true, HashPreprocessor = true,
        }), ".vb", ".vbs", ".bas", ".vba", ".frm");
        Map(() => new CodeLanguage("shell", "Shell", new CodeLanguageDefinition
        {
            LineComments = ["#"], Strings = [Dq(true), new("'", "'", '\0', true)], Keywords = ShellKeywords, Literals = "true false", VariablePrefixes = "$",
        }), ".sh", ".bash", ".zsh", ".ksh", ".fish", ".csh", ".tcsh", ".bashrc", ".zshrc", ".profile", ".bash_profile", ".envrc", ".command");
        Map(() => new CodeLanguage("powershell", "PowerShell", new CodeLanguageDefinition
        {
            LineComments = ["#"], BlockComments = [("<#", "#>")],
            Strings = [new("@\"", "\"@", '`', true), new("@'", "'@", '\0', true), new("\"", "\"", '`', true, true), new("'", "'", '\0', true, true)],
            Keywords = PowerShellKeywords, Literals = "$true $false $null", CaseInsensitive = true, VariablePrefixes = "$", WordCharacters = "-",
        }), ".ps1", ".psm1", ".psd1", ".ps1xml", ".pssc", ".psrc");
        Map(() => new CodeLanguage("batch", "Batch", new CodeLanguageDefinition
        {
            LineComments = ["::", "REM ", "@REM "], Strings = [new("\"", "\"", '\0')], Keywords = BatchKeywords, CaseInsensitive = true, VariablePrefixes = "%!", Literals = string.Empty,
        }), ".bat", ".cmd", ".btm");
        Map(() => new CodeLanguage("dockerfile", "Dockerfile", new CodeLanguageDefinition
        {
            LineComments = ["#"], Strings = [Dq(), Sq()], Keywords = DockerKeywords, VariablePrefixes = "$",
        }), ".dockerfile", ".containerfile");
        Map(() => new CodeLanguage("make", "Makefile", new CodeLanguageDefinition
        {
            LineComments = ["#"], Strings = [Dq(), Sq()], Keywords = MakeKeywords, VariablePrefixes = "$",
        }), ".mk", ".mak", ".make");
        Map(() => new CodeLanguage("cmake", "CMake", new CodeLanguageDefinition
        {
            LineComments = ["#"], BlockComments = [("#[[", "]]")], Strings = [Dq(true)], Keywords = CMakeKeywords, CaseInsensitive = true, VariablePrefixes = "$",
        }), ".cmake");
        Map(() => new CodeLanguage("latex", "LaTeX", new CodeLanguageDefinition
        {
            LineComments = ["%"], Keywords = LatexKeywords, VariablePrefixes = "\\", Literals = string.Empty,
        }), ".tex", ".sty", ".ltx", ".bib", ".dtx", ".ins");
        Map(() => new CodeLanguage("asm", "Assembly", new CodeLanguageDefinition
        {
            LineComments = [";", "//"], BlockComments = [("/*", "*/")], Strings = [Dq(), Sq()], Keywords = AsmKeywords, CaseInsensitive = true, HashPreprocessor = false, Literals = string.Empty,
        }), ".asm", ".s", ".nasm", ".inc");
        Map(() => new CodeLanguage("razor", "Razor", new CodeLanguageDefinition
        {
            LineComments = ["//"], BlockComments = [("@*", "*@"), ("<!--", "-->")], Strings = [Dq(), Sq()], Keywords = CSharpKeywords, Decorators = true, CapitalizedTypes = true,
        }), ".razor", ".cshtml", ".vbhtml");

        Map(() => new MarkupLanguage("xml", "XML"),
            ".xml", ".xaml", ".axaml", ".xsd", ".xsl", ".xslt", ".wsdl", ".dtd", ".plist", ".nuspec", ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".vcxitems",
            ".filters", ".proj", ".props", ".targets", ".projitems", ".shproj", ".sqlproj", ".wixproj", ".wxs", ".wxi", ".wxl", ".resx", ".config", ".manifest",
            ".appxmanifest", ".pubxml", ".runsettings", ".ruleset", ".xlf", ".xliff", ".rss", ".atom", ".kml", ".gpx", ".opml", ".storyboard", ".xib", ".mxml", ".tmx", ".tld", ".jrxml", ".pom", ".fxml", ".sitemap", ".ui", ".qrc");
        Map(() => new MarkupLanguage("html", "HTML"), ".html", ".htm", ".xhtml", ".xht", ".shtml", ".vue", ".svelte", ".astro", ".hbs", ".handlebars", ".mustache", ".jinja", ".j2", ".twig", ".liquid", ".njk", ".ejs");
        Map(() => new CssLanguage("css", "CSS"), ".css", ".scss", ".sass", ".less", ".styl", ".pcss", ".postcss");
        Map(() => new YamlLanguage(), ".yaml", ".yml", ".clang-format", ".clang-tidy");
        Map(() => new IniLanguage("toml", "TOML", toml: true), ".toml");
        Map(() => new IniLanguage("ini", "INI"),
            ".ini", ".cfg", ".conf", ".cnf", ".properties", ".prefs", ".env", ".editorconfig", ".gitconfig", ".gitmodules", ".npmrc", ".yarnrc", ".pylintrc",
            ".flake8", ".desktop", ".reg", ".inf", ".service", ".socket", ".timer", ".mount", ".target", ".hgrc", ".htaccess", ".pgpass");
        Map(() => new MarkdownSourceLanguage(), ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdwn", ".mdx", ".rmarkdown");
        Map(() => new DiffLanguage(), ".diff", ".patch", ".rej");
        Map(() => new LogLanguage(), ".log", ".out", ".err", ".trace");
        Map(() => new CodeLanguage("ignore", "Ignore", new CodeLanguageDefinition { LineComments = ["#"], Literals = string.Empty }),
            ".gitignore", ".gitattributes", ".dockerignore", ".npmignore", ".eslintignore", ".prettierignore", ".hgignore", ".vscodeignore", ".gcloudignore", ".slugignore", ".helmignore", ".stylelintignore");

        Name(".cs", "Directory.Build.rsp");
        Name(".dockerfile", "Dockerfile", "Containerfile");
        Name(".mk", "Makefile", "makefile", "GNUmakefile", "Kbuild");
        Name(".cmake", "CMakeLists.txt");
        Name(".rb", "Rakefile", "Gemfile", "Podfile", "Vagrantfile", "Brewfile", "Fastfile", "Guardfile");
        Name(".py", "SConstruct", "SConscript", "BUILD", "WORKSPACE", "Snakefile");
        Name(".sh", "PKGBUILD", "APKBUILD", ".bashrc", ".zshrc", ".profile", ".bash_profile", ".bash_logout", ".zprofile", ".envrc");
        Name(".ini", ".editorconfig", ".gitconfig", ".gitmodules", ".npmrc", ".env", ".htaccess", "hosts", "desktop.ini", ".yarnrc", ".pylintrc", ".flake8");
        Name(".gitignore", ".gitignore", ".gitattributes", ".dockerignore", ".npmignore", ".eslintignore", ".prettierignore", "CODEOWNERS");
        Name(".json", ".babelrc", ".eslintrc", ".prettierrc", "composer.lock", "package-lock.json", "Pipfile.lock", ".jshintrc", ".swcrc");
        Name(".toml", "Pipfile", "Cargo.lock", "poetry.lock", "uv.lock");
        Name(".yaml", "yarn.lock", ".clang-format", ".clang-tidy", "pnpm-lock.yaml");
    }

    /// <summary>The language of <paramref name="path"/> by its name, then its extension; null for plain text.</summary>
    public static SyntaxLanguage? ForPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = Path.GetFileName(path);
        if (ByFileName.TryGetValue(name, out Func<SyntaxLanguage>? byName))
        {
            return Get(byName);
        }

        string extension = Path.GetExtension(path);
        if (extension.Length > 1 && ByExtension.TryGetValue(extension, out Func<SyntaxLanguage>? byExtension))
        {
            return Get(byExtension);
        }

        // Dotfiles with no further extension: ".bashrc" is its own extension.
        return null;
    }

    /// <summary>The language with a given identifier ("markdown", "html"), for source views.</summary>
    public static SyntaxLanguage? ById(string id)
    {
        foreach (Func<SyntaxLanguage> factory in ByExtension.Values.Distinct())
        {
            SyntaxLanguage language = Get(factory);
            if (language.Id == id)
            {
                return language;
            }
        }

        return null;
    }

    /// <summary>File names (not extensions) that have a language: "Makefile", "Dockerfile", ".gitignore"…</summary>
    public static bool HasKnownFileName(string fileName) => ByFileName.ContainsKey(fileName);

    /// <summary>Extensions that have a language (".cs", ".json", …).</summary>
    public static IReadOnlyCollection<string> Extensions => ByExtension.Keys;

    private static void Map(Func<SyntaxLanguage> factory, params string[] extensions)
    {
        Func<SyntaxLanguage> shared = factory;
        foreach (string extension in extensions)
        {
            ByExtension[extension] = shared;
        }
    }

    /// <summary>File names that take the language of <paramref name="extension"/>.</summary>
    private static void Name(string extension, params string[] names)
    {
        Func<SyntaxLanguage> factory = ByExtension[extension];
        foreach (string name in names)
        {
            ByFileName[name] = factory;
        }
    }

    private static SyntaxLanguage Get(Func<SyntaxLanguage> factory)
    {
        lock (Gate)
        {
            if (!Created.TryGetValue(factory, out SyntaxLanguage? language))
            {
                language = factory();
                Created[factory] = language;
            }

            return language;
        }
    }
}
