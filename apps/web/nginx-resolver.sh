#!/bin/sh
# Runs from /docker-entrypoint.d before nginx starts: point nginx at the container network's DNS so `api` is
# re-resolved (valid=10s) and a recreated api container does not leave the proxy on a stale address.
ns="$(awk '/^nameserver/ { print $2; exit }' /etc/resolv.conf)"
echo "resolver ${ns:-127.0.0.11} valid=10s ipv6=off;" > /etc/nginx/conf.d/resolver.conf
