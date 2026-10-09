# 0039: Docker Hub token is repository-scoped and lives in a tag-only GitHub environment

- **Status:** Accepted
- **Date:** 2026-10-04
- **Area:** —
- **Source:** Issue #9
- **Supersedes:** —

## Context

Issue #9 asks for the Docker Hub credentials to be stored as repository secrets, with an access token
limited to pushing `networlddev/vandox`. Record 0027 says the release workflow needs Docker Hub credentials
as CI secrets.

A Docker Hub personal access token cannot be limited to one repository: its scopes (*Read*,
*Read & Write*, *Read, Write & Delete*) apply to every repository its user can reach. A plain repository
secret is readable by any workflow run that a person with write access starts on any branch. It is not
readable by fork pull requests or by Dependabot.

A tag push runs the workflow file *as it is in the tagged commit*, and an environment's deployment rule
matches only the ref name. Anyone who can create a `v*.*.*` tag can therefore tag a side-branch commit
carrying a modified `release.yml`, for example one without the ancestry check or one that prints the token.
The environment alone does not prevent this. The repository is public and owned by a user account. On
2026-10-04 its only ruleset was the branch ruleset `main-Protection`.

## Options considered

1. **Where the token is stored**
   - *Plain repository secret*: matches the issue's wording literally, but any branch workflow can read it.
   - *Secret of a GitHub environment `release` whose deployment rule allows only tags `v*.*.*`*: only a
     job that declares the environment and runs for such a tag can read it.

   Chosen: the environment. It is still a secret stored in this repository, and it narrows exposure to the
   one publishing job.
2. **What token is used**
   - *An organization access token of `networlddev`, limited to the repository `networlddev/vandox` with
     push and pull*: meets "this repository only".
   - *A dedicated Docker Hub user in a team with Read & Write on `networlddev/vandox` only, with a Read &
     Write personal access token*: effectively repository-scoped. Fallback if organization access tokens are
     not available.
   - *A personal access token of the maintainer's account*: account-wide. Rejected.
3. **How the user name is stored**
   - *As a secret*: GitHub would mask `networlddev` everywhere in the logs, including the image name.
   - *As an environment variable `DOCKERHUB_USERNAME`*: it is not confidential.

   Chosen: the variable.
4. **Who may start a release (who may create `v*` tags)**
   - *Anyone with write access* (no tag protection): the environment would protect nothing against a
     collaborator, as described above.
   - *A tag ruleset on `refs/tags/v*` that restricts creation, update and deletion, with only Repository
     admin as bypass*: only the owner, who controls the token anyway, can start a release.
   - *Additionally, a required reviewer on environment `release`*: stops even an admin's tag until someone
     approves. With a single maintainer this only adds a manual click on their own release, which goes
     against issue #9's "without manual steps".

   Chosen: the tag ruleset. The required reviewer is optional, and recommended once a second person has
   write access.

## Decision

The token is stored as the secret `DOCKERHUB_TOKEN` of the GitHub environment `release`, with a deployment
rule for tags `v*.*.*` only. A repository tag ruleset `release-tags` on `refs/tags/v*` restricts creating,
moving and deleting those tags to the Repository admin role. The maintainer creates it before storing the
token. The login name is the environment variable `DOCKERHUB_USERNAME`.

Only the `publish-image` job of `.github/workflows/release.yml` declares the environment. It passes the
token to `docker login --password-stdin`, never on a command line, and runs `docker logout` at the end. The
token is an organization access token limited to `networlddev/vandox`, or, if the subscription has none,
a dedicated user's Read & Write token whose team rights cover only that repository. `docs/CONTRIBUTING.md`
describes creating and rotating it.

## Consequences

- Branch and pull-request workflow runs, including the release dry run, cannot read the token.
- A leaked token can push to `networlddev/vandox` only, not to other repositories of the organization.
  Within that repository it can still overwrite tags. The workflow itself never overwrites a version tag
  (0041). Where the Docker Hub subscription offers it, the maintainer can enable immutable tags on
  `networlddev/vandox` for the version-tag pattern only (not `latest`, which has to move); Docker Hub then
  refuses to overwrite a published version even with a leaked token. This is optional and recommended, and
  is described in `docs/CONTRIBUTING.md`.
- Creating the ruleset, the environment, the token and its scope is a manual step for the maintainer. It
  cannot be verified from the repository. The ruleset can be checked with `gh api repos/{owner}/{repo}/rulesets`.
- Residual risk: the boundary for "published only from `main`" is the tag ruleset. The in-workflow ancestry
  check (0037) cannot be that boundary. Anyone allowed to create `v*` tags controls the workflow run that
  reads the token. That is the admin today; adding a bypass actor extends it. Without the ruleset, every
  collaborator with write access could publish.
- If the Docker Hub subscription offers neither organization access tokens nor teams, the "this
  repository only" scope cannot be reached. Accepting an account-wide token then needs a new record that
  states the residual.
