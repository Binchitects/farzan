# The plugin runtime: plugins' server code, each plugin a worker of its own (supervisor.mjs says how it is kept apart).
# Node only, no packages: nothing to download but the base image, which the offline bundle carries.
FROM node:22-alpine
COPY supervisor.mjs /opt/plugins/supervisor.mjs
# Root, to give each worker its own uid; compose takes every capability but those away, and the network.
USER root
ENTRYPOINT ["node", "/opt/plugins/supervisor.mjs"]
