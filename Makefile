VERSION := 0.17.10.0

release:
	@echo "Updating version to $(VERSION) in build.yaml..."
	sed -i 's/^version: .*/version: "$(VERSION)"/' build.yaml
	@echo "Committing changes..."
	git add build.yaml
	git commit -m "Bump version to $(VERSION)"
	@echo "Pushing to git..."
	git push
	@echo "Creating GitHub release..."
	gh release create v$(VERSION) --title "v$(VERSION)" --generate-notes

.PHONY: release