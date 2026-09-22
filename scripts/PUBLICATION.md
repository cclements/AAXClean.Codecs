# Build versus publication status

The reusable build defaults publish to false. PR validation builds and retains
artifacts without attempting NuGet publication. The existing master-push caller
explicitly requests publication, preserving that established delivery trigger.
An omitted publish input is validation only; its green result is not delivery.

Requested publication requires a credential and existing archive. The helper
passes arguments literally and returns the actual dotnet nuget push exit status.
There is no continue-on-error or skip-duplicate override: rejection, conflict,
missing credentials and transport failures fail the job. The helper never prints
the credential itself. Existing pack/admission gates run before this step.

Four local process tests use a synthetic dotnet executable, dummy package and
inert URL. They prove no client invocation for missing credentials/archives,
exact arguments, successful exit and propagation of exit codes 1 and 17.
They do not exercise GitHub Actions or an actual NuGet server. Package retrieval,
independent payload inspection and downstream resolution remain necessary before
a publication can be called delivered; an accepted push alone is insufficient.
No package is published by running the tests.

Workflow syntax reference:
https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax
