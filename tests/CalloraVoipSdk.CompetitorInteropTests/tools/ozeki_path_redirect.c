#define _GNU_SOURCE

#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static const char source_prefix[] =
    "/usr/share/Ozeki.{20d04fe0-3aea-1069-a2d8-08002b30309d}";
static const char target_suffix[] =
    "/Ozeki.{20d04fe0-3aea-1069-a2d8-08002b30309d}";
static const char default_target_root[] =
    "/tmp/mini-core-compare-ozeki";

static const char *redirect_path(
    const char *path,
    char redirected[PATH_MAX])
{
    const char *target_root;
    size_t prefix_length;
    int written;

    if (path == NULL)
    {
        return NULL;
    }

    prefix_length = sizeof(source_prefix) - 1;
    if (strncmp(path, source_prefix, prefix_length) != 0
        || (path[prefix_length] != '\0' && path[prefix_length] != '/'))
    {
        return path;
    }

    target_root = getenv("MINI_CORE_OZEKI_DATA_ROOT");
    if (target_root == NULL || target_root[0] != '/')
    {
        target_root = default_target_root;
    }

    written = snprintf(
        redirected,
        PATH_MAX,
        "%s%s%s",
        target_root,
        target_suffix,
        path + prefix_length);
    if (written < 0 || written >= PATH_MAX)
    {
        errno = ENAMETOOLONG;
        return NULL;
    }

    return redirected;
}

static void *resolve_symbol(const char *name)
{
    void *symbol = dlsym(RTLD_NEXT, name);
    if (symbol == NULL)
    {
        const char message[] = "Ozeki path redirect could not resolve a libc symbol.\n";
        (void)write(STDERR_FILENO, message, sizeof(message) - 1);
        _exit(127);
    }

    return symbol;
}

int mkdir(const char *path, mode_t mode)
{
    static int (*original)(const char *, mode_t);
    char redirected[PATH_MAX];
    const char *mapped;

    if (original == NULL)
    {
        original = resolve_symbol("mkdir");
    }

    mapped = redirect_path(path, redirected);
    return mapped == NULL ? -1 : original(mapped, mode);
}

static mode_t read_mode(int flags, va_list arguments)
{
    if ((flags & O_CREAT) != 0 || (flags & O_TMPFILE) == O_TMPFILE)
    {
        return va_arg(arguments, mode_t);
    }

    return 0;
}

int open(const char *path, int flags, ...)
{
    static int (*original)(const char *, int, ...);
    char redirected[PATH_MAX];
    const char *mapped;
    va_list arguments;
    mode_t mode;

    if (original == NULL)
    {
        original = resolve_symbol("open");
    }

    mapped = redirect_path(path, redirected);
    if (mapped == NULL)
    {
        return -1;
    }

    va_start(arguments, flags);
    mode = read_mode(flags, arguments);
    va_end(arguments);
    return ((flags & O_CREAT) != 0 || (flags & O_TMPFILE) == O_TMPFILE)
        ? original(mapped, flags, mode)
        : original(mapped, flags);
}

int open64(const char *path, int flags, ...)
{
    static int (*original)(const char *, int, ...);
    char redirected[PATH_MAX];
    const char *mapped;
    va_list arguments;
    mode_t mode;

    if (original == NULL)
    {
        original = resolve_symbol("open64");
    }

    mapped = redirect_path(path, redirected);
    if (mapped == NULL)
    {
        return -1;
    }

    va_start(arguments, flags);
    mode = read_mode(flags, arguments);
    va_end(arguments);
    return ((flags & O_CREAT) != 0 || (flags & O_TMPFILE) == O_TMPFILE)
        ? original(mapped, flags, mode)
        : original(mapped, flags);
}

int __xstat64(int version, const char *path, struct stat64 *buffer)
{
    static int (*original)(int, const char *, struct stat64 *);
    char redirected[PATH_MAX];
    const char *mapped;

    if (original == NULL)
    {
        original = resolve_symbol("__xstat64");
    }

    mapped = redirect_path(path, redirected);
    return mapped == NULL ? -1 : original(version, mapped, buffer);
}

int __lxstat64(int version, const char *path, struct stat64 *buffer)
{
    static int (*original)(int, const char *, struct stat64 *);
    char redirected[PATH_MAX];
    const char *mapped;

    if (original == NULL)
    {
        original = resolve_symbol("__lxstat64");
    }

    mapped = redirect_path(path, redirected);
    return mapped == NULL ? -1 : original(version, mapped, buffer);
}

int unlink(const char *path)
{
    static int (*original)(const char *);
    char redirected[PATH_MAX];
    const char *mapped;

    if (original == NULL)
    {
        original = resolve_symbol("unlink");
    }

    mapped = redirect_path(path, redirected);
    return mapped == NULL ? -1 : original(mapped);
}

int rename(const char *old_path, const char *new_path)
{
    static int (*original)(const char *, const char *);
    char redirected_old[PATH_MAX];
    char redirected_new[PATH_MAX];
    const char *mapped_old;
    const char *mapped_new;

    if (original == NULL)
    {
        original = resolve_symbol("rename");
    }

    mapped_old = redirect_path(old_path, redirected_old);
    mapped_new = redirect_path(new_path, redirected_new);
    return mapped_old == NULL || mapped_new == NULL
        ? -1
        : original(mapped_old, mapped_new);
}
