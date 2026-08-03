using System.Collections.Generic;
using netDxf;

namespace CadToolsApp.Models
{
    // A MULTILEADER callout parsed directly from the raw DXF (netDxf cannot read this entity type).
    // AnchorPoints are the leader arrowhead locations — where the callout actually points at, in
    // document (model/paper space) coordinates — used for proximity matching against geometry.
    public record CalloutAnnotation(string Layer, string Text, IReadOnlyList<Vector3> AnchorPoints);

    // One cable-duct polyline after classification. Length feeds the per-entity cable-type total
    // directly; RouteGroupId feeds the digging-method total, which must be summed per physical
    // route (not per entity) since a real "2xDB7" run is commonly drawn as two coincident/parallel
    // duct polylines sharing one trench — see CableClassifier for the clustering rule.
    public record ClassifiedCable(
        string Handle,
        double Length,
        string? BaseType,       // "DB7"/"DB2"/"HDPE"/"COAX", or null if the legend couldn't resolve it
        string? Stripe,         // "GREY"/"ORANGE"/"GREEN"/diameter token, or null if no callout evidence
        string? Method,         // MethodRows key ("SLEUF","HANDBORING",...), or null if no callout evidence
        string RouteGroupId,
        string Evidence,        // matched callout text, or a reason string when nothing matched
        bool IsExisting,        // classified as "bestaande" (existing) infrastructure — excluded from totals
        bool NearScopeBoundary); // some vertices fell outside the project scope box — review before trusting
}
