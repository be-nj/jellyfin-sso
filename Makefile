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

.PHONY: build publish deploy clean

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
