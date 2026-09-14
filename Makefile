RELEASE_FLAGS ?=
NOTES_FILE ?= /tmp/release_notes.md

# Print the next version git-cliff would cut, e.g. v0.26.19.0. Stderr is silenced so the
# output can be captured directly: v="$(make --no-print-directory version)".
version:
	@git cliff --bumped-version 2>/dev/null

# Write the release notes for VERSION to NOTES_FILE.
notes:
	@test -n "$(VERSION)" || { echo "VERSION is required, e.g. make notes VERSION=v1.2.3.4" >&2; exit 1; }
	@git cliff --unreleased --tag $(VERSION) --strip all 2>/dev/null > $(NOTES_FILE)
	@echo "Release notes for $(VERSION) written to $(NOTES_FILE)"

# Bump build.yaml to VERSION, commit, push, and create the GitHub Release.
# This is the first step with permanent effects. Run tests and package verification first.
publish:
	@test -n "$(VERSION)" || { echo "VERSION is required, e.g. make publish VERSION=v1.2.3.4" >&2; exit 1; }
	@test -s "$(NOTES_FILE)" || { echo "$(NOTES_FILE) is missing or empty; run make notes VERSION=$(VERSION) first" >&2; exit 1; }
	sed -i.bak 's/^version: .*/version: "$(VERSION:v%=%)"/' build.yaml && rm -f build.yaml.bak
	git add build.yaml
	git commit -m "chore(release): bump version to $(VERSION)"
	git push
	gh release create $(VERSION) --title "$(VERSION)" --notes-file $(NOTES_FILE) $(RELEASE_FLAGS)
	@echo "Release $(VERSION) created successfully!"

# Local end-to-end release: compute the version, write notes, publish.
# The CI workflow does NOT use this target; it runs tests and package checks between
# `version` and `publish`.
release:
	@set -e; \
	git fetch --tags; \
	v="$$(git cliff --bumped-version 2>/dev/null)"; \
	echo "New version will be: $$v"; \
	$(MAKE) --no-print-directory notes VERSION=$$v; \
	$(MAKE) --no-print-directory publish VERSION=$$v RELEASE_FLAGS="$(RELEASE_FLAGS)"

prerelease: RELEASE_FLAGS := --prerelease
prerelease: release

# Show the version and notes `make release` would produce, without changing anything.
release-preview:
	@set -e; \
	git fetch --tags; \
	v="$$(git cliff --bumped-version 2>/dev/null)"; \
	echo "New version will be: $$v"; \
	$(MAKE) --no-print-directory notes VERSION=$$v; \
	cat $(NOTES_FILE)

test:
	dotnet test Gelato.sln

.PHONY: version notes publish release prerelease release-preview test
