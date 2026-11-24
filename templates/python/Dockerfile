FROM python:3.12-alpine
WORKDIR /app
COPY connector ./connector
RUN adduser -D app && mkdir -p /app/data && chown app /app/data
USER app
EXPOSE 8080
HEALTHCHECK CMD wget -qO- http://127.0.0.1:8080/health || exit 1
CMD ["python", "-m", "connector.server"]
