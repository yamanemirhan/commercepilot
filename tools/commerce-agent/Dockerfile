FROM node:24-alpine
WORKDIR /app
COPY tools/commerce-agent/package*.json ./
RUN npm ci && npm cache clean --force
COPY tools/commerce-agent/src ./src
USER node
EXPOSE 8080
CMD ["node", "--import", "tsx", "src/web-server.ts"]
