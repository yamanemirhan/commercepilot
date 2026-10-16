FROM postgres:18-alpine AS extension
ARG PGVECTOR_VERSION=0.8.7
RUN apk add --no-cache build-base git \
    && git clone --branch v${PGVECTOR_VERSION} --depth 1 https://github.com/pgvector/pgvector.git /tmp/pgvector \
    && cd /tmp/pgvector && make with_llvm=no OPTFLAGS="" && make with_llvm=no install

FROM postgres:18-alpine
COPY --from=extension /usr/local/lib/postgresql/vector.so /usr/local/lib/postgresql/vector.so
COPY --from=extension /usr/local/share/postgresql/extension/vector* /usr/local/share/postgresql/extension/
