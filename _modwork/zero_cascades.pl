local $/; my $x=<>;
$x =~ s{(<dir_shadow_num_cascades>)([^<]*)(</dir_shadow_num_cascades>)}{
  my($o,$m,$c)=($1,$2,$3); $m=~s/-?\d+(?:\.\d+)?/0.0000/g; "$o$m$c";
}ge;
print $x;
