namespace redb.Route.Tests.Expressions;

/// <summary>
/// The recorded behaviour of the expression language, one line per form of
/// <see cref="ExpressionCorpus"/>, four columns for the four positions a string can occupy.
/// <para>
/// This is a characterisation record, not a wish list: it says what the language does today,
/// including where it is wrong. Its job is to make any change to expression handling visible as a
/// diff. A line that moves must be explained before this file is updated — that is the whole
/// point of keeping it.
/// </para>
/// <para>
/// To regenerate: run <c>ExpressionLanguageSnapshotTests</c>, read the reported differences, and
/// when they are all intended copy in the fresh snapshot the failure message points to.
/// </para>
/// </summary>
internal static class ExpressionLanguageSnapshot
{
    internal const string Expected =
        """
        # Characterisation of the redb.Route expression language.
        #
        # One line per form of the corpus, four columns, one per position:
        #   value     — StringExpression(form), what SetBody / SetHeader / a connector option does
        #   condition — PredicateFactory.FromString(form), what Filter / When / LoopWhile / Validate does
        #   template  — StringExpression("${form}"), the interpolated form
        #
        # This file records what the language does TODAY, right or wrong. A diff here means
        # behaviour moved; every moved line has to be explained before the snapshot is updated.

        ## literals
        42 | value=42:Int32 | condition=true | template=42:Int32
        -7 | value=-7:Double | condition=true | template=-7:Double
        2.5 | value=2.5:Double | condition=true | template=2.5:Double
        true | value=true | condition=true | template=true
        false | value=false | condition=false | template=false
        'hello' | value="hello" | condition=true | template="hello"
        "hello" | value="hello" | condition=true | template="hello"
        'a > b' | value="a > b" | condition=true | template="a > b"
        'x AND y' | value="x AND y" | condition=true | template="x AND y"
        hello | value="hello" | condition=true | template=null
        plain text | value="plain text" | condition=true | template=null

        ## config-shaped literals
        a>b | value=false | condition=false | template=false
        x<y | value=false | condition=false | template=false
        a==b | value=true | condition=true | template=true
        dGVzdC1zZWNyZXQ= | value="dGVzdC1zZWNyZXQ=" | condition=true | template=null
        dGVzdC1zZWNyZXQ== | value=!ExpressionCompilationException | condition=!ExpressionCompilationException | template=!ExpressionCompilationException
        <root/> | value=!ExpressionCompilationException | condition=!ExpressionCompilationException | template=!ExpressionCompilationException
        <xml> | value=!ExpressionCompilationException | condition=!ExpressionCompilationException | template=!ExpressionCompilationException
        key=value | value="key=value" | condition=true | template=null
        application/json | value=null | condition=false | template=null
        text/plain; charset=utf-8 | value=null | condition=false | template=null
        some/path/file.txt | value=null | condition=false | template=null
        https://example.com/a?b=c | value=null | condition=false | template=null
        2026-08-28 | value=1990:Int32 | condition=true | template=1990:Int32
        black and white | value=false | condition=false | template=false
        yes or no | value=false | condition=false | template=false
        BLACK AND WHITE | value=false | condition=false | template=false

        ## accessors
        header.a | value=42:Int32 | condition=true | template=42:Int32
        header.zero | value=0:Int32 | condition=false | template=0:Int32
        header.neg | value=-7:Int32 | condition=true | template=-7:Int32
        header.flag | value=true | condition=true | template=true
        header.off | value=false | condition=false | template=false
        header.s | value="hello" | condition=true | template="hello"
        header.empty | value="" | condition=false | template=""
        header.missing | value=null | condition=false | template=null
        header.dotted.key | value="literal-dot" | condition=true | template="literal-dot"
        header.Content-Type | value=null | condition=false | template="text/xml"
        header.X-Request-Id | value=null | condition=false | template="req-1"
        header.a+b | value="42b" | condition=true | template="plus-name"
        property.count | value=7:Int32 | condition=true | template=7:Int32
        property.zero | value=0:Int32 | condition=false | template=0:Int32
        property.flag | value=false | condition=false | template=false
        property.missing | value=null | condition=false | template=null
        body @Text | value="payload" | condition=true | template="payload"
        body | value=<Person> | condition=true | template=<Person>
        exception.Message | value="exception.Message" | condition=true | template="boom"
        contentType | value=null | condition=false | template=null

        ## member access
        header.user.Name | value="ann" | condition=true | template="ann"
        header.user.Age | value=30:Int32 | condition=true | template=30:Int32
        header.user.Active | value=true | condition=true | template=true
        header.user.Manager | value=null | condition=false | template=null
        header.list.Count | value=5:Int32 | condition=true | template=5:Int32
        header.s.Length | value=5:Int32 | condition=true | template=5:Int32
        property.user.Name | value="ann" | condition=true | template="ann"
        property.cfg.enabled | value=true | condition=true | template=true
        property.cfg.limit | value=5:Int32 | condition=true | template=5:Int32
        property.items.Count | value=3:Int32 | condition=true | template=3:Int32
        body.Name | value="ann" | condition=true | template="ann"
        body.Age | value=30:Int32 | condition=true | template=30:Int32
        body.Manager.Name | value="bob" | condition=true | template="bob"
        header.user.Level | value=3:Int32 | condition=true | template=3:Int32
        property.user.Level | value=3:Int32 | condition=true | template=3:Int32
        body.Level | value=3:Int32 | condition=true | template=3:Int32
        body.Manager.Level | value=3:Int32 | condition=true | template=3:Int32

        ## comparison spaced
        header.a > 10 | value=true | condition=true | template=true
        header.a < 10 | value=false | condition=false | template=false
        header.a >= 42 | value=true | condition=true | template=true
        header.a <= 42 | value=true | condition=true | template=true
        header.a == 42 | value=true | condition=true | template=true
        header.a != 42 | value=false | condition=false | template=false
        header.s == 'hello' | value=true | condition=true | template=true
        header.s != 'hello' | value=false | condition=false | template=false
        header.missing == null | value=true | condition=true | template=true
        header.a != null | value=true | condition=true | template=true
        1 > 0 | value=true | condition=true | template=true

        ## comparison whitespace
        header.a>10 | value=true | condition=true | template=true
        header.a>=42 | value=true | condition=true | template=true
        header.a<=42 | value=true | condition=true | template=true
        header.a==42 | value=true | condition=true | template=true
        header.a!=42 | value=false | condition=false | template=false
        header.a  >  10 | value=true | condition=true | template=true
        header.a\t>\t10 | value=true | condition=true | template=true
        header.a\n>\n10 | value=true | condition=true | template=true
          header.a>10   | value=true | condition=true | template=true
        header.a >10 | value=true | condition=true | template=true
        header.a> 10 | value=true | condition=true | template=true
        1>0 | value=true | condition=true | template=true

        ## comparison member
        header.user.Age > 18 | value=true | condition=true | template=true
        header.user.Age>18 | value=true | condition=true | template=true
        header.list.Count > 3 | value=true | condition=true | template=true
        property.cfg.limit > 3 | value=true | condition=true | template=true
        property.cfg.limit>3 | value=true | condition=true | template=true
        property.items.Count > 2 | value=true | condition=true | template=true
        header.s.Length > 3 | value=true | condition=true | template=true
        body.Age > 18 | value=true | condition=true | template=true
        body.Age>18 | value=true | condition=true | template=true

        ## quoted operators
        header.quoted == 'a > b' | value=true | condition=true | template=true
        header.quoted=='a > b' | value=true | condition=true | template=true
        header.quoted == 'a < b' | value=false | condition=false | template=false
        header.worded == 'x AND y' | value=true | condition=true | template=true
        header.worded == 'x OR y' | value=false | condition=false | template=false
        header.ct == 'application/json' | value=true | condition=true | template=true
        header.ct=='application/json' | value=true | condition=true | template=true

        ## word logic
        header.a > 10 AND header.b < 5 | value=true | condition=true | template=true
        header.a>10 AND header.b<5 | value=true | condition=true | template=true
        header.a>10  AND  header.b<5 | value=true | condition=true | template=true
        header.a>10\tAND\theader.b<5 | value=true | condition=true | template=true
        header.a>10 and header.b<5 | value=true | condition=true | template=true
        header.a > 10 OR header.b > 100 | value=true | condition=true | template=true
        header.a > 10 XOR header.b > 100 | value=true | condition=true | template=true
        property.count > 5 OR property.flag | value=true | condition=true | template=true
        NOT header.flag | value=false | condition=false | template=false
        !header.flag | value=false | condition=false | template=false
        header.a > 10 AND NOT header.off | value=true | condition=true | template=true
        header.a > 10 AND !header.off | value=true | condition=true | template=true
        header.a AND header.b | value=true | condition=true | template=true

        ## parentheses
        (header.a > 10) | value=true | condition=true | template=true
        (header.a > 10) AND (header.b < 5) | value=true | condition=true | template=true
        (header.a>10) AND (header.b<5) | value=true | condition=true | template=true
        (header.a > 10 AND header.b < 5) | value=true | condition=true | template=true
        (header.a > 100) OR (header.b < 5) | value=true | condition=true | template=true
        ((header.a > 10)) | value=true | condition=true | template=true
        (header.a + header.b) * 2 | value=90:Int32 | condition=true | template=90:Int32

        ## arithmetic
        header.a + header.b | value=45:Int32 | condition=true | template=45:Int32
        header.a+header.b | value=45:Int32 | condition=true | template=45:Int32
        header.a - 2 | value=40:Int32 | condition=true | template=40:Int32
        header.a-2 | value=40:Int32 | condition=true | template=40:Int32
        header.a * 2 | value=84:Int32 | condition=true | template=84:Int32
        header.a*2 | value=84:Int32 | condition=true | template=84:Int32
        header.a / 2 | value=21:Int32 | condition=true | template=21:Int32
        header.a/2 | value=21:Int32 | condition=true | template=21:Int32
        header.a + header.b * 2 | value=48:Int32 | condition=true | template=48:Int32
        header.dbl * 2 | value=5:Int32 | condition=true | template=5:Int32
        -header.a | value=-42:Double | condition=true | template=-42:Double
        header.a + header.b > 40 | value=true | condition=true | template=true
        header.a+header.b>40 | value=true | condition=true | template=true
        header.a % 5 | value=2:Int32 | condition=true | template=2:Int32
        header.a%5 | value=2:Int32 | condition=true | template=2:Int32
        header.neg % 3 | value=-1:Int32 | condition=true | template=-1:Int32
        header.dbl % 2 | value=0.5:Double | condition=true | template=0.5:Double
        header.a % header.zero | value=null | condition=false | template=null
        header.a % 2 == 0 | value=true | condition=true | template=true
        header.a%2==0 | value=true | condition=true | template=true

        ## functions
        count(property.items) | value=3:Int32 | condition=true | template=3:Int32
        count(property.items) > 2 | value=true | condition=true | template=true
        count(property.items)>2 | value=true | condition=true | template=true
        length(property.name) | value=4:Int32 | condition=true | template=4:Int32
        length(property.name) > 3 | value=true | condition=true | template=true
        contains(header.s, 'ell') | value=true | condition=true | template=true
        upper(header.s) | value="HELLO" | condition=true | template="HELLO"
        lower(header.s) | value="hello" | condition=true | template="hello"
        logical(header.a > 10) | value=true | condition=true | template=true
        logical(header.a>10) | value=true | condition=true | template=true
        logical(header.a > 100) | value=false | condition=false | template=false
        logical((header.a > 10) AND (header.b < 5)) | value=true | condition=true | template=true
        logical(header.a + header.b > 40) | value=true | condition=true | template=true
        logical(header.worded == 'x AND y') | value=true | condition=true | template=true
        jpath($.order.id) @Json | value=!ExpressionCompilationException | condition=!ExpressionCompilationException | template=7:Int64
        xpath(/order/id) @Xml | value=!ExpressionCompilationException | condition=!ExpressionCompilationException | template=7:Int32
        datediff('2026-01-03', '2026-01-01', 'days') | value=2:Int32 | condition=true | template=2:Int32
        datediff('2026-01-01', '2026-01-03', 'hours') | value=-48:Int32 | condition=true | template=-48:Int32
        datediff('2026-01-01T06:00:00', '2026-01-01T00:00:00', 'days') | value=0.25:Double | condition=true | template=0.25:Double
        datediff('2026-01-02', '2026-01-01', 'fortnights') | value=null | condition=false | template=null

        ## increments
        header.a++ | value=42:Int32 | condition=true | template=42:Int32
        ++header.a | value=43:Int32 | condition=true | template=43:Int32
        header.a-- | value=42:Int32 | condition=true | template=42:Int32
        --header.a | value=41:Int32 | condition=true | template=41:Int32
        property.count++ | value=7:Int32 | condition=true | template=7:Int32
        ++property.count | value=8:Int32 | condition=true | template=8:Int32

        ## min max
        min(header.a, header.b) | value=3:Double | condition=true | template=3:Double
        max(header.a, header.b) | value=42:Double | condition=true | template=42:Double
        min(header.list) | value=1:Double | condition=true | template=1:Double
        max(header.list) | value=5:Double | condition=true | template=5:Double
        min(property.items) | value=null | condition=false | template=null
        max(property.missing) | value=null | condition=false | template=null

        ## decimal literals
        max(2.5, 1) | value=2.5:Double | condition=true | template=2.5:Double
        round(2.567, 2) | value=2.57:Double | condition=true | template=2.57:Double
        abs(-2.5) | value=2.5:Double | condition=true | template=2.5:Double
        2.5 + 1 | value=3.5:Double | condition=true | template=3.5:Double

        ## ternary
        header.a > 10 ? 'big' : 'small' | value="big" | condition=true | template="big"
        header.a>10 ? 'big' : 'small' | value="big" | condition=true | template="big"
        header.a > 100 ? 'big' : 'small' | value="small" | condition=true | template="small"
        length(property.name) > 3 ? true : false | value=true | condition=true | template=true
        header.missing ?? 5 | value=5:Int32 | condition=true | template=5:Int32
        header.a ?? 5 | value=42:Int32 | condition=true | template=42:Int32
        header.missing ?? 'dflt' | value="dflt" | condition=true | template="dflt"

        ## index access
        property.items[0] | value="p" | condition=true | template="p"
        header.list[1] | value=2:Int32 | condition=true | template=2:Int32
        property.items[0] == 'p' | value=true | condition=true | template=true

        ## whole-string placeholder
        ${header.flag} | value=true | condition=true | template=-
        ${header.off} | value=false | condition=false | template=-
        ${header.zero} | value=0:Int32 | condition=false | template=-
        ${header.empty} | value="" | condition=false | template=-
        ${header.missing} | value=null | condition=false | template=-
        ${header.a > 10} | value=true | condition=true | template=-
        ${header.a > 100} | value=false | condition=false | template=-
        ${header.a>100} | value=false | condition=false | template=-
        ${header.s == 'hello'} | value=true | condition=true | template=-
        ${header.s == 'nope'} | value=false | condition=false | template=-
        ${header.a > 10 AND header.b < 5} | value=true | condition=true | template=-

        ## camel idioms
        ${header.a} > 10 | value="42 > 10" | condition=true | template=-
        ${header.a} > 100 | value="42 > 100" | condition=true | template=-
        ${header.a} == 42 | value="42 == 42" | condition=true | template=-
        ${header.s} == 'hello' | value="hello == 'hello'" | condition=true | template=-
        ${header.s} == 'nope' | value="hello == 'nope'" | condition=true | template=-
        ${header.a} > 10 && ${header.b} < 5 | value="42 > 10 && 3 < 5" | condition=true | template=-
        ${header[Content-Type]} | value=null | condition=false | template=-
        ${headers.Content-Type} | value=null | condition=false | template=-
        ${headers[Content-Type]} | value=null | condition=false | template=-
        ${exchangeProperty.count} | value=null | condition=false | template=-
        ${property.count} | value=7:Int32 | condition=true | template=-
        ${body} @Text | value="payload" | condition=true | template=-

        ## truthiness
        0 | value=0:Int32 | condition=false | template=0:Int32
        1 | value=1:Int32 | condition=true | template=1:Int32
        '0' | value="0" | condition=false | template="0"
        '1' | value="1" | condition=true | template="1"
        'no' | value="no" | condition=false | template="no"
        'yes' | value="yes" | condition=true | template="yes"
        'off' | value="off" | condition=false | template="off"
        'oui' | value="oui" | condition=true | template="oui"
        'nein' | value="nein" | condition=true | template="nein"
        'anything else' | value="anything else" | condition=true | template="anything else"
        header.dbl | value=2.5:Double | condition=true | template=2.5:Double
        header.zero AND header.a | value=false | condition=false | template=false
        header.zero OR header.a | value=true | condition=true | template=true
        logical(header.zero) | value=false | condition=false | template=false
        logical(header.a) | value=true | condition=true | template=true
        NOT header.zero | value=true | condition=true | template=true

        ## malformed
        header.a > | value=!ExpressionCompilationException | condition=!ExpressionCompilationException | template=null
        > 10 | value=!ExpressionCompilationException | condition=!ExpressionCompilationException | template=!ExpressionCompilationException
        (((( | value="((((" | condition=true | template=!ExpressionCompilationException
        1 + | value="1" | condition=true | template=!ExpressionCompilationException
        ~~~INVALID~~~ | value="~~~INVALID~~~" | condition=true | template=!ExpressionCompilationException
        header. | value=null | condition=false | template=null
        count( | value=!ExpressionCompilationException | condition=!ExpressionCompilationException | template=!ExpressionCompilationException
        'unterminated | value="'unterminated" | condition=true | template="unterminate"
        """;
}
