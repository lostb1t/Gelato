release:
	@echo "Fetching tags..."
	git fetch --tags
	@echo "Bumping version with git-cliff..."
	$(eval NEW_VERSION := $(shell git cliff --bumped-version))
	@echo "New version will be: $(NEW_VERSION)"
	@echo "Updating version in build.yaml..."
	sed -i 's/^version: .*/version: "$(NEW_VERSION:v%=%)"/' build.yaml
	git add build.yaml
	git commit -m "chore(release): bump version to $(NEW_VERSION)"
	@echo "Generating changelog..."
	$(eval PREV_TAG := $(shell git describe --tags --abbrev=0 2>/dev/null || echo ""))
	$(eval CHANGELOG := $(shell git cliff $(PREV_TAG)..HEAD --strip all))
	@echo "Pushing to git..."
	git push
	@echo "Creating GitHub release..."
	gh release create $(NEW_VERSION) --title "$(NEW_VERSION)" --notes "$(CHANGELOG)"
	@echo "Release $(NEW_VERSION) created successfully!"

test:
	@echo "Fetching tags..."
	git fetch --tags
	@echo "Bumping version with git-cliff..."
	$(eval NEW_VERSION := $(shell git cliff --bumped-version))
	@echo "New version will be: $(NEW_VERSION)"

  
.PHONY: release

