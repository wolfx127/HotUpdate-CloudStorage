# Build context is the project root.
FROM node:20-alpine
WORKDIR /app
COPY CloudServer/ /app/
COPY ServerData/ /app/ServerData/
ENV PORT=8080
ENV HOST=0.0.0.0
ENV DATA_DIR=/app/CloudServer/data
EXPOSE 8080
CMD ["node", "server.js"]
