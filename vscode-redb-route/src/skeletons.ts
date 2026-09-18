/**
 * The composite skeletons the schema cannot offer (a document's xmlns, a route with its
 * consumer, both branches of a choice, catch+finally). Single elements are NOT here on
 * purpose: LemMinX builds those from the XSD with required attributes and the closing tag.
 * Served by our own completion provider — declarative snippets cannot be scoped to the
 * new-element position or to our documents only.
 */
export type Skeleton = {
    label: string;
    detail: string;
    /** VSCode snippet syntax; always starts with `<`. */
    body: string;
};

export const SKELETONS: Skeleton[] = [
    {
        label: "<routes",
        detail: "redb routes document",
        body: [
            "<routes xmlns=\"urn:redb:route:1.0\">",
            "\t<route id=\"${1:route-id}\">",
            "\t\t<from uri=\"${2:direct://in}\"/>",
            "\t\t$0",
            "\t</route>",
            "</routes>",
        ].join("\n"),
    },
    {
        label: "<route",
        detail: "redb route with consumer",
        body: [
            "<route id=\"${1:route-id}\" description=\"${2}\">",
            "\t<from uri=\"${3:direct://in}\"/>",
            "\t$0",
            "</route>",
        ].join("\n"),
    },
    {
        label: "<choice",
        detail: "redb choice with branches",
        body: [
            "<choice>",
            "\t<when expr=\"${1:header.kind == 'a'}\">",
            "\t\t$0",
            "\t</when>",
            "\t<otherwise>",
            "\t</otherwise>",
            "</choice>",
        ].join("\n"),
    },
    {
        label: "<tryCatch",
        detail: "redb tryCatch with handlers",
        body: [
            "<tryCatch>",
            "\t$0",
            "\t<catch exception=\"${1:System.Exception}\">",
            "\t</catch>",
            "\t<finally>",
            "\t</finally>",
            "</tryCatch>",
        ].join("\n"),
    },
];
