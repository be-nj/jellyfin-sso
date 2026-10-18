PLUGIN_SRC  := Jellyfin.Plugin.Sso
PUBLISH_DIR := /tmp/jellyfin-sso-publish

# Jellyfin runs in Docker on diana.home.
# Config volume: /etc/komodo/stacks/jellyfin/jellyfin-config -> /config in container.
JELLYFIN_HOST        ?= diana.home
JELLYFIN_PLUGIN_PATH ?= /etc/komodo/stacks/jellyfin/jellyfin-config/plugins/SSO Authentication_1.0.0.0

# External deps that Jellyfin does NOT bundle (must live in the plugin folder).
EXTRA_DLLS := IdentityModel.dll \
              Microsoft.IdentityModel.Abstractions.dll \
              Microsoft.IdentityModel.JsonWebTokens.dll \
              Microsoft.IdentityModel.Logging.dll \
              Microsoft.IdentityModel.Protocols.dll \
              Microsoft.IdentityModel.Protocols.OpenIdConnect.dll \
              Microsoft.IdentityModel.Tokens.dll \
              System.IdentityModel.Tokens.Jwt.dll

.PHONY: build publish deploy test e2e clean

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

test:
	dotnet test tests/Jellyfin.Plugin.Sso.Tests -c Release --logger trx

# The e2e suite starts a throwaway Jellyfin container, a fake identity provider and a
# headless browser. There is no local dotnet SDK, so it runs inside the SDK image: the
# repo is mounted at its own path so that bind mounts created from inside still resolve
# on the host's docker daemon, and the host network makes the fake provider reachable.
e2e:
	docker run --rm --network host \
	  -v /var/run/docker.sock:/var/run/docker.sock \
	  -v "$(CURDIR)":"$(CURDIR)" -w "$(CURDIR)" \
	  -e HOME=/tmp -e NUGET_PACKAGES="$(CURDIR)/tests/runs/nuget" \
	  -e E2E_PLAYWRIGHT_WITH_DEPS=1 \
	  mcr.microsoft.com/dotnet/sdk:10.0 \
	  bash -c 'apt-get update -qq && apt-get install -y -qq zip >/dev/null \
	    && dotnet test tests/Jellyfin.Plugin.Sso.E2eTests -c Release --logger trx'
