#!/usr/bin/env bash
# Refreshes catalog.json from poe2db: names, images and stack sizes; codes for new items.
# Existing codes are kept (keyed by category + url), so hand edits survive. overrides.json is applied first.
# New items get a generated 2-4 letter code, unique case-insensitively across all categories (plus the category's
# one-letter prefix). Must give the same result as update-catalog.ps1. Needs curl and perl (with JSON::PP, a core
# module).
#
# usage: update-catalog.sh [--pages-dir DIR] [--catalog FILE] [--overrides FILE]
#   --pages-dir  read <Page>.html from DIR instead of downloading (offline runs, tests)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pages_dir=""
catalog="$here/catalog.json"
overrides="$here/overrides.json"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --pages-dir) pages_dir="$2"; shift 2 ;;
        --catalog) catalog="$2"; shift 2 ;;
        --overrides) overrides="$2"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

pages=(Stackable_Currency Augment Omen Liquid_Emotions Essence Catalysts)
if [[ -z "$pages_dir" ]]; then
    pages_dir="$(mktemp -d)"
    trap 'rm -rf "$pages_dir"' EXIT
    for page in "${pages[@]}"; do
        curl -fsS -A "Ww.ExiledData currency catalog (personal tool)" -o "$pages_dir/$page.html" "https://poe2db.tw/us/$page"
        sleep 0.5
    done
fi

PAGES_DIR="$pages_dir" CATALOG="$catalog" OVERRIDES="$overrides" exec perl -CSDA -Mutf8 -e '
use strict; use warnings; use feature "unicode_strings"; use JSON::PP;

my $url_base = "https://poe2db.tw/us/";
my $image_base = "https://cdn.poe2db.tw/image/Art/2DItems/";
# poe2db page -> category, code prefix
# Emotions: no prefix, the leading l comes from the "Liquid" marker (overrides.json), the same on the Currency page.
my @pages = ([Stackable_Currency => "Currency", ""], [Augment => "Augment", "a"], [Omen => "Omen", "o"],
    [Liquid_Emotions => "Emotions", ""], [Essence => "Essence", "e"], [Catalysts => "Catalysts", "c"]);
my %prefix = map { $_->[1] => $_->[2] } @pages;
my %keep = map { $_ => 1 } qw(StackableCurrency SoulCore Omen);
my %stop = map { $_ => 1 } qw(of the a an and);
my %ent = (amp => "&", lt => "<", gt => ">", quot => "\"", apos => "\x27", nbsp => " ");
my $card = qr{<div class="d-flex border-top rounded"><div class="flex-shrink-0"><a class="([^"]*)"[^>]*href="([^"]+)"><img[^>]*src="([^"]+)"[^>]*></a></div><div class="flex-grow-1 ms-2"><a[^>]*>(?:<img[^>]*>)?([\s\S]*?)</a>([\s\S]*?)(?=<div class="d-flex border-top rounded"|<div class="col|$)};

sub text {
    my $s = shift;
    $s =~ s/<[^>]+>/ /g;
    $s =~ s{&(#x?[0-9a-fA-F]+|\w+);?}{
        my $e = $1;
        $e =~ /^#[xX](.+)/ ? chr(hex $1) : $e =~ /^#(.+)/ ? chr($1) : exists $ent{$e} ? $ent{$e} : "&$e;"
    }ge;
    $s =~ s/\s+/ /g; $s =~ s/^ | $//g;
    return $s;
}
sub strip { my ($s, $base) = @_; defined $s && index($s, $base) == 0 ? substr($s, length $base) : $s }
sub slurp { my $f = shift; open my $h, "<:encoding(UTF-8)", $f or die "$f: $!\n"; local $/; <$h> }

# --- read pages -------------------------------------------------------------------------------------------------
my @fresh;
for my $p (@pages) {
    my ($page, $category) = @$p;
    my $html = slurp("$ENV{PAGES_DIR}/$page.html");
    my (%seen, $count);
    while ($html =~ /$card/g) {
        my ($cls, $href, $img, $name, $body) = ($1, $2, $3, $4, $5);
        my $class = (split " ", $cls)[-1] // "";
        next if $seen{$href}++ || !$keep{$class};
        my $stack;
        if ($body =~ m{Stack Size: <span[^>]*>([\s\S]*?)</span>}) {
            my $max = (split m{/}, text($1))[-1] // "";
            $max =~ s/^\s+|\s+$//g;
            $stack = 0 + $max if $max =~ /^\d+$/;
        }
        push @fresh, { name => text($name), category => $category, url => strip($href, $url_base),
            image => strip($img, $image_base), maxStackSize => $stack };
        $count++;
    }
    printf "%-10s %d\n", $category, $count // 0;
}
die "No items found: the page layout may have changed.\n" unless @fresh;

# --- codes ------------------------------------------------------------------------------------------------------
my @old = -e $ENV{CATALOG} ? @{ decode_json(do { my $s = slurp($ENV{CATALOG}); utf8::encode($s); $s }) } : ();
my (%override, @markers);
if (-e $ENV{OVERRIDES}) {
    my $s = slurp($ENV{OVERRIDES}); utf8::encode($s);
    my $o = decode_json($s);
    $override{$_} = $o->{$_} for grep { $_ ne "_comment" && $_ ne "markers" } keys %$o;
    # Marker phrases, longest first, so "Concentrated Liquid" wins over "Liquid".
    my $m = $o->{markers} // {};
    @markers = map { [$_, $m->{$_}] } sort { length $b <=> length $a or $a cmp $b } grep { $_ ne "_comment" } keys %$m;
}

my %owner;       # lower-case base code -> who holds it ("url:..", "name:..", "tier:base|t")
my %code_by_url; # url -> base code
my %code_by_name;# name -> base code
my %tiers_of;    # base name -> [tier letters present]
my %overridden;  # lower-case codes set by overrides.json

sub tier_of { $_[0] =~ /^(Lesser|Greater|Perfect) (.+)$/ ? { base => $2, t => lc substr($1, 0, 1) } : undef }
sub override_of {
    my $name = shift;
    return $override{$name} if exists $override{$name};
    my $t = tier_of($name);
    return $t && exists $override{$t->{base}} ? $override{$t->{base}} . $t->{t} : undef;
}
sub claim {
    my ($code, $who) = @_;
    my $k = lc $code;
    die "Code \x27$code\x27 for $who is already taken by $owner{$k}.\n" if exists $owner{$k} && $owner{$k} ne $who;
    $owner{$k} = $who;
}

for my $it (@fresh) {
    my $t = tier_of($it->{name}) or next;
    my $list = $tiers_of{$t->{base}} //= [];
    push @$list, $t->{t} unless grep { $_ eq $t->{t} } @$list;
}

# Overrides first: they win over everything, including codes already in the catalog.
for my $it (@fresh) {
    my $o = override_of($it->{name}) // next;
    claim($o, "url:$it->{url}");
    $overridden{lc $o} = 1;
    $code_by_url{$it->{url}} = $o;
    $code_by_name{$it->{name}} = $o unless tier_of($it->{name});
}
for my $it (@old) {
    # A blank code asks for a new one.
    next if !defined $it->{code} || $it->{code} eq "" || exists $code_by_url{$it->{url}};
    my $p = $prefix{$it->{category}} // "";
    my $base = $p ne "" && index($it->{code}, $p) == 0 ? substr($it->{code}, length $p) : $it->{code};
    # Hand-made codes are kept even when they clash; new codes avoid both.
    my $k = lc $base;
    die "overrides.json gives $owner{$k} the code of $it->{code} ($it->{name}); change one of them.\n"
        if $overridden{$k} && $owner{$k} ne "url:$it->{url}";
    if (exists $owner{$k} && $owner{$k} ne "url:$it->{url}") {
        warn "WARNING: $it->{code} ($it->{name}) clashes with $owner{$k} when case is ignored.\n";
    } else {
        $owner{$k} = "url:$it->{url}";
    }
    $code_by_url{$it->{url}} = $base;
    $code_by_name{$it->{name}} //= $base;
}

# Candidates, best first: initials, initials with one or two words extended, a middle word dropped, then the
# first letter with letters picked from the rest of the name. A number in the name is kept as digits.
sub candidates {
    my $name = shift;
    my @out;
    if ($name =~ /(\d+)/) {
        my $num = $1;
        (my $plain = $name) =~ s/\(.*?\)|\d+//g;
        my $ini = join "", map { uc substr($_, 0, 1) } grep { /^[A-Za-z]/ && !$stop{lc $_} } split /\s+/, $plain;
        my $n = 4 - length $num; $n = 1 if $n < 1;
        push @out, substr($ini, 0, $n) . $num if $ini ne "";
    }
    (my $clean = $name) =~ s/\x27s\b//g;
    $clean =~ s/[^A-Za-z\s-]//g;
    my @all = grep { $_ ne "" } split /[\s-]+/, $clean;
    my @words = grep { !$stop{lc $_} } @all;
    my @ws = @words ? @words : @all;
    @ws = @ws[0 .. 3] if @ws > 4;
    my $up = sub { uc substr($_[0], 0, 1) };
    my $rest = sub { lc substr($_[0], 1) };
    my $n = @ws;
    if ($n > 1) {
        push @out, join "", map { $up->($_) } @ws;
        for my $k (1, 2) {
            for (my $i = $n - 1; $i >= 0; $i--) {
                push @out, join "", map { $up->($ws[$_]) . ($_ == $i ? substr($rest->($ws[$_]), 0, $k) : "") } 0 .. $n - 1;
            }
        }
        for my $i (0 .. $n - 1) {
            for my $j ($i + 1 .. $n - 1) {
                push @out, join "", map { $up->($ws[$_]) . ($_ == $i || $_ == $j ? substr($rest->($ws[$_]), 0, 1) : "") } 0 .. $n - 1;
            }
        }
        if ($n > 2) {
            for my $i (1 .. $n - 2) {
                push @out, candidates(join " ", map { $ws[$_] } grep { $_ != $i } 0 .. $n - 1);
            }
        }
    }
    my $head = $up->($ws[0]);
    my @tail = split //, $rest->($ws[0]) . lc join "", @ws[1 .. $n - 1];
    my $len = @tail;
    for my $i (0 .. $len - 1) { push @out, $head . $tail[$i] }
    for my $i (0 .. $len - 1) { for my $j ($i + 1 .. $len - 1) { push @out, $head . $tail[$i] . $tail[$j] } }
    for my $i (0 .. $len - 1) { for my $j ($i + 1 .. $len - 1) { for my $k ($j + 1 .. $len - 1) {
        push @out, $head . $tail[$i] . $tail[$j] . $tail[$k] } } }
    return @out;
}

# A name holding a marker phrase: the phrase is dropped and its letters lead the code (Liquid Paranoia -> lP).
sub marker_code {
    my $it = shift;
    for my $m (@markers) {
        my ($phrase, $letters) = @$m;
        my $re = qr/(?:^|\s)\Q$phrase\E(?=\s|$)/;
        next unless $it->{name} =~ $re;
        (my $rest = $it->{name}) =~ s/$re/ /g;
        $rest =~ s/\s+/ /g; $rest =~ s/^ | $//g;
        return undef if $rest eq "";
        my $initials = join "", map { uc substr($_, 0, 1) } grep { /^[A-Za-z]/ && !$stop{lc $_} } split /\s+/, $rest;
        for my $c ($initials, candidates($rest)) {
            next if $c eq "" || length $c > 4;
            return $letters . $c unless exists $owner{lc($letters . $c)};
        }
        die "No free code for $it->{name}.\n";
    }
    return undef;
}

# A base name s code, generated once; its tier codes (code + l/g/p) are reserved with it.
sub base_code {
    my $name = shift;
    return $code_by_name{$name} if exists $code_by_name{$name};
    my @tiers = @{ $tiers_of{$name} // [] };
    for my $c (candidates($name)) {
        next if length $c < 2 || length $c > 4;
        next if grep { exists $owner{lc $_} } $c, map { $c . $_ } @tiers;
        $owner{lc $c} = "name:$name";
        $owner{lc($c . $_)} = "tier:$name|$_" for @tiers;
        return $code_by_name{$name} = $c;
    }
    die "No free code for $name.\n";
}

sub code_for {
    my $it = shift;
    return $code_by_url{$it->{url}} if exists $code_by_url{$it->{url}};
    my $c = marker_code($it);
    my $t = defined $c ? undef : tier_of($it->{name});
    if ($t) {
        my $candidate = base_code($t->{base}) . $t->{t};
        my $o = $owner{lc $candidate};
        $c = $candidate if !defined $o || $o eq "tier:$t->{base}|$t->{t}";
    }
    # Not a tier, or its tier code is taken by a hand-made code: a code of its own.
    $c //= base_code($it->{name});
    $owner{lc $c} = "url:$it->{url}";
    $code_by_url{$it->{url}} = $c;
    print "new: $it->{category} $it->{name} -> $prefix{$it->{category}}$c\n";
    return $c;
}

$_->{code} = $prefix{$_->{category}} . code_for($_) for @fresh;

# Items gone from poe2db stay (old stashes still hold them), after their category s current items.
my %fresh_key = map { ("$_->{category}|$_->{url}" => 1) } @fresh;
my @result;
for my $p (@pages) {
    my $category = $p->[1];
    push @result, grep { $_->{category} eq $category } @fresh;
    for my $it (grep { $_->{category} eq $category && !$fresh_key{"$_->{category}|$_->{url}"} } @old) {
        print "kept (not on poe2db): $it->{category} $it->{name}\n";
        push @result, $it;
    }
}

# --- write, formatted like JSON.stringify(list, null, 2) --------------------------------------------------------
sub json {
    my ($v, $number) = @_;
    return "null" unless defined $v;
    return 0 + $v if $number;
    (my $s = $v) =~ s/(["\\])/\\$1/g;
    $s =~ s/([\x00-\x1f])/sprintf("\\u%04x", ord $1)/ge;
    return "\"$s\"";
}
my @fields = qw(name code category url image maxStackSize);
my $text = @result
    ? "[\n" . join(",\n", map { my $it = $_; "  {\n" . join(",\n", map { "    \"$_\": " . json($it->{$_}, $_ eq "maxStackSize") } @fields) . "\n  }" } @result) . "\n]"
    : "[]";
open my $out, ">:raw:encoding(UTF-8)", $ENV{CATALOG} or die "$ENV{CATALOG}: $!\n";
print $out $text;
close $out;
printf "total      %d -> %s\n", scalar @result, $ENV{CATALOG};
'
