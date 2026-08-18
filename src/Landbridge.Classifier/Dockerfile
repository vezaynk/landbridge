FROM node:22-bookworm-slim
WORKDIR /app
COPY classify.mjs server.mjs prompt.mjs llm.mjs ./
COPY vendor/qwen-readonly.mjs vendor/NOTICE ./vendor/
ENV PORT=5310
EXPOSE 5310
USER node
CMD ["node", "server.mjs"]
