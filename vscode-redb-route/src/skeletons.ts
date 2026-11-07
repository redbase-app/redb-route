/**
 * The composite skeletons the schema cannot offer (a route with its consumer, both branches of a
 * choice, catch+finally). Single elements are NOT here on purpose: LemMinX builds those from the
 * XSD with required attributes and the closing tag. Served by our own completion provider —
 * declarative snippets cannot be scoped to the new-element position or to our documents only.
 *
 * Every skeleton says WHERE it may be inserted (see skeletonscope.ts). The provider used to offer
 * all of them at every element position, so `<choice>` and `<tryCatch>` were suggested directly
 * under `<routes>` — the loader refuses those ("<choice> is not valid at the container level") and
 * the schema never offered them (owner finding 2026-10-08).
 */

/** Where a skeleton may be inserted: beside the routes, or as a step. */
export type SkeletonPlace = "document-root" | "step-content";

export type Skeleton = {
    label: string;
    detail: string;
    place: SkeletonPlace;
    /** VSCode snippet syntax; always starts with `<`. */
    body: string;
};

/** The skeletons of one place, in list order. */
export function skeletonsFor(place: SkeletonPlace): Skeleton[] {
    return SKELETONS.filter(skeleton => skeleton.place === place);
}

export const SKELETONS: Skeleton[] = [
    {
        label: "<route",
        detail: "redb route with consumer",
        place: "document-root",
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
        place: "step-content",
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
        place: "step-content",
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
