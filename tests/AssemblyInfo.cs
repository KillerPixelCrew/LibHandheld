// Native fixtures share the active device diagnostic sink and process-wide transport seams.
// Keep each owning lifetime isolated, including its background readers and observation loop.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
