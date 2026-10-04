# 0039: Docker Hub token is repository-scoped and lives in a tag-only GitHub environment

- **Status:** Proposed
- **Date:** 2026-10-04
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

## Decision

The token is stored as the secret `DOCKERHUB_TOKEN` of the GitHub environment `release`, with a deployment
rule for tags `v*.*.*` only. The login name is the environment variable `DOCKERHUB_USERNAME`.

Only the `publish-image` job of `.github/workflows/release.yml` declares the environment. It passes the
token to `docker login --password-stdin`, never on a command line, and runs `docker logout` at the end. The
token is an organization access token limited to `networlddev/vandox`, or, if the subscription has none,
a dedicated user's Read & Write token whose team rights cover only that repository. `docs/CONTRIBUTING.md`
describes creating and rotating it.

## Consequences

- Branch and pull-request workflow runs, including the release dry run, cannot read the token.
- A leaked token can push to `networlddev/vandox` only, not to other repositories of the organization.
  Within that repository it can still overwrite tags. The workflow itself never overwrites a version tag
  (0038).
- Creating the environment, the token and its scope is a manual step for the maintainer and cannot be
  verified from the repository.
- If the Docker Hub subscription offers neither organization access tokens nor teams, the "this
  repository only" scope cannot be reached. Accepting an account-wide token then needs a new record that
  states the residual.
