local $/; my $x = <>;
my @tags = qw(
  fog_density fog_alpha fog_haze_density fog_haze_alpha
  fogvolume_density_scalar fogvolume_density_scalar_interior fogvolume_fog_scaler
  shadow_distance_mult dir_shadow_distance_multiplier
  cloud_shadow_density cloud_shadow_opacity sky_cloud_shadow_strength
  lens_artefacts_intensity lens_artefacts_max_exp_intensity lens_artefacts_min_exp_intensity
);
for my $t (@tags) {
  $x =~ s{(<\Q$t\E>)([^<]*)(</\Q$t\E>)}{
    my ($o,$m,$c) = ($1,$2,$3);
    $m =~ s/-?\d+(?:\.\d+)?/0.0000/g;
    "$o$m$c";
  }ge;
}
print $x;
