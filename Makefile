PLUGIN_SRC  := Jellyfin.Plugin.Sso
PUBLISH_DIR := /tmp/jellyfin-sso-publish

# Jellyfin runs in Docker on diana.home.
# Config volume: /etc/komodo/stacks/jellyfin/jellyfin-config -> /config in container.
JELLYFIN_HOST        ?= diana.home
JELLYFIN_PLUGIN_PATH ?= /etc/komodo/stacks/jellyfin/jellyfin-config/plugins/SSO Authentication_1.0.0.0

# External deps that Jellyfin does NOT bundle (must live in the plugin folder).
EXTRA_DLLS := Duende.IdentityModel.dll \
              Microsoft.IdentityModel.Abstractions.dll \
              Microsoft.IdentityModel.JsonWebTokens.dll \
              Microsoft.IdentityModel.Logging.dll \
              Microsoft.IdentityModel.Protocols.dll \
              Microsoft.IdentityModel.Protocols.OpenIdConnect.dll \
              Microsoft.IdentityModel.Tokens.dll \
              System.IdentityModel.Tokens.Jwt.dll

.PHONY: build publish deploy test e2e screenshots demo clean

build:
	dotnet build $(PLUGIN_SRC) -c Release

publish:
	dotnet publish $(PLUGIN_SRC) -c Release -o $(PUBLISH_DIR)

# Stop the container first: overwriting a loaded plugin DLL makes the old process
# crash with BadImageFormatException while it shuts down.
deploy: publish
	ssh $(JELLYFIN_HOST) 'sudo mkdir -p "$(JELLYFIN_PLUGIN_PATH)"'
	ssh $(JELLYFIN_HOST) "docker stop -t 30 jellyfin"
	scp $(PUBLISH_DIR)/Jellyfin.Plugin.Sso.dll \
	    $(foreach dll,$(EXTRA_DLLS),$(PUBLISH_DIR)/$(dll)) \
	    "$(JELLYFIN_HOST):$(JELLYFIN_PLUGIN_PATH)/"
	ssh $(JELLYFIN_HOST) "docker start jellyfin"

clean:
	rm -rf $(PLUGIN_SRC)/bin $(PLUGIN_SRC)/obj

# Unit tests. There is no local dotnet SDK, so they run inside the SDK image like the
# e2e suite; CI, which has the SDK, runs the same command without the wrapper.
test:
	docker run --rm \
	  -v "$(CURDIR)":"$(CURDIR)" -w "$(CURDIR)" \
	  -e HOME=/tmp -e NUGET_PACKAGES="$(CURDIR)/tests/runs/nuget" \
	  mcr.microsoft.com/dotnet/sdk:10.0.402 \
	  dotnet test tests/Jellyfin.Plugin.Sso.Tests -c Release --logger trx

# Mutation testing: changes the code (a < into a <=, a condition into its opposite) and
# counts how many of those changes the tests notice. It answers the question a passing
# suite cannot — whether the tests would catch a defect at all. Slow, minutes rather than
# seconds, so it is not part of `make test`.
mutants:
	docker run --rm \
	  -v "$(CURDIR)":"$(CURDIR)" -w "$(CURDIR)" \
	  -e HOME=/tmp -e NUGET_PACKAGES="$(CURDIR)/tests/runs/nuget" \
	  mcr.microsoft.com/dotnet/sdk:10.0.402 \
	  bash -c 'rm -rf tests/runs/stryker \
	    && dotnet tool install -g dotnet-stryker >/dev/null \
	    && export PATH="$$PATH:/tmp/.dotnet/tools" \
	    && scripts/mutants.sh'

# The e2e suite starts a throwaway Jellyfin container, a fake identity provider and a
# headless browser. E2E_FILTER passes a vstest filter through, e.g.
#   make e2e E2E_FILTER='--filter "Category=RealIdp"'   # only Keycloak and Authelia
#   make e2e E2E_FILTER='--filter "Category!=RealIdp"'  # skip the container-heavy tier There is no local dotnet SDK, so it runs inside the SDK image: the
# repo is mounted at its own path so that bind mounts created from inside still resolve
# on the host's docker daemon, and the host network makes the fake provider reachable.
# Regenerates the screenshots the README embeds, from the real plugin page.
screenshots:
	$(MAKE) e2e E2E_FILTER='--filter "Category=Screenshots"'

# Records the login clip the README shows, against a real Authentik, and turns the raw
# webm into the GIF and the MP4 that are committed. Minutes, because authentik starts
# four containers — run it when the login looks different, not on every change.
demo:
	$(MAKE) e2e E2E_FILTER='--filter "Category=Demo"'
	scripts/demo-clip.sh

e2e:
	docker run --rm --network host \
	  -e E2E_JELLYFIN_TAG="$(E2E_JELLYFIN_TAG)" \
	  -v /var/run/docker.sock:/var/run/docker.sock \
	  -v "$(CURDIR)":"$(CURDIR)" -w "$(CURDIR)" \
	  -e HOME=/tmp -e NUGET_PACKAGES="$(CURDIR)/tests/runs/nuget" \
	  -e E2E_PLAYWRIGHT_WITH_DEPS=1 \
	  mcr.microsoft.com/dotnet/sdk:10.0.402 \
	  bash -c 'apt-get update -qq && apt-get install -y -qq zip docker.io >/dev/null \
	    && dotnet test tests/Jellyfin.Plugin.Sso.E2eTests -c Release --logger trx $(E2E_FILTER)'
