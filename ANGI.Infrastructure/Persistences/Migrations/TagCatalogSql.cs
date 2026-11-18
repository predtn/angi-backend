namespace ANGI.Infrastructure.Persistences.Migrations;

/// <summary>
/// Moves an existing tag catalogue onto the six tag types (ANGI_8_TagType_Mon_An_Dong_Bo.xlsx, sheet "Script dữ liệu").
/// Ids and codes of the original tags never change, because dish_tags, menu_submission_items.tag_ids and Agent
/// state keep tag ids. local_sf_main stays a category tag: the Agent finds the main dish by it, and each dish that
/// has it also gets role.mon_chinh. A database without the original catalogue (new or test database) is left
/// untouched; the seed loads the whole catalogue there.
/// </summary>
internal static class TagCatalogSql
{
    public const string Up = """
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM core.tags WHERE tag_type = 'category' AND code = 'local_sf_main') THEN
                RETURN;
            END IF;

            UPDATE core.tags SET tag_type = 'ingredient' WHERE tag_type = 'dietary' AND code IN ('pork', 'beef', 'seafood');

            UPDATE core.tags AS t SET name = v.name
            FROM (VALUES
                ('meal_time', 'breakfast', 'Ăn sáng'),
                ('meal_time', 'lunch', 'Ăn trưa'),
                ('meal_time', 'snack', 'Ăn vặt'),
                ('meal_time', 'dinner', 'Ăn tối'),
                ('meal_time', 'late_night', 'Ăn đêm'),
                ('dietary', 'vegetarian', 'Món chay'),
                ('category', 'local_sf_dessert', 'Tráng miệng'),
                ('category', 'local_sf_drink', 'Đồ uống'),
                ('category', 'local_sf_snack', 'Đồ ăn vặt'),
                ('ingredient', 'beef', 'Thịt bò'),
                ('ingredient', 'pork', 'Thịt heo'),
                ('ingredient', 'seafood', 'Hải sản')
            ) AS v(tag_type, code, name)
            WHERE t.tag_type = v.tag_type AND t.code = v.code;

            -- Catalogues loaded with explicit ids leave the identity sequence behind.
            PERFORM setval(pg_get_serial_sequence('core.tags', 'id'), GREATEST((SELECT COALESCE(MAX(id), 0) FROM core.tags), 1));

            INSERT INTO core.tags (tag_type, code, name) VALUES
                ('dietary', 'diet.thuan_chay', 'Thuần chay'),
                ('category', 'form.mon_com_va_ngu_coc', 'Cơm & xôi'),
                ('category', 'form.mon_soi', 'Bún, phở & mì'),
                ('category', 'form.banh_mi_va_banh_kep', 'Bánh mì & bánh kẹp'),
                ('category', 'form.canh_va_sup', 'Canh & súp'),
                ('category', 'form.lau', 'Lẩu'),
                ('category', 'form.chao', 'Cháo'),
                ('category', 'form.banh_bot_va_mon_nhan', 'Bánh & món bột'),
                ('category', 'form.goi_va_salad', 'Gỏi & salad'),
                ('category', 'form.do_an_kem_va_thanh_phan_ban_rieng', 'Đồ ăn kèm'),
                ('category', 'form.muc_menu_nhieu_cau_phan', 'Combo & set'),
                ('category', 'form.khac_cho_duyet', 'Khác'),
                ('meal_role', 'role.mon_chinh', 'Món chính'),
                ('meal_role', 'role.bua_du_trong_mot_mon', 'Đủ một bữa'),
                ('meal_role', 'role.mon_dam_dung_voi_mon_khac', 'Món đạm ăn kèm'),
                ('meal_role', 'role.mon_rau_an_kem', 'Rau ăn kèm'),
                ('meal_role', 'role.mon_tinh_bot_an_kem', 'Tinh bột ăn kèm'),
                ('meal_role', 'role.mon_canh_sup_an_kem', 'Canh ăn kèm'),
                ('meal_role', 'role.khai_vi', 'Khai vị'),
                ('meal_role', 'role.an_vat', 'Món ăn vặt'),
                ('meal_role', 'role.trang_mieng', 'Tráng miệng'),
                ('meal_role', 'role.do_uong', 'Đồ uống'),
                ('cooking_method', 'method.chien_chua_ro_kieu', 'Chiên/rán'),
                ('cooking_method', 'method.nuong', 'Nướng'),
                ('cooking_method', 'method.xao', 'Xào'),
                ('cooking_method', 'method.hap', 'Hấp'),
                ('cooking_method', 'method.luoc', 'Luộc'),
                ('cooking_method', 'method.kho', 'Kho'),
                ('cooking_method', 'method.ham_va_ninh', 'Hầm/ninh'),
                ('cooking_method', 'method.tron', 'Trộn'),
                ('cooking_method', 'method.ap_chao', 'Áp chảo'),
                ('ingredient', 'ingredient.thit_ga', 'Thịt gà'),
                ('ingredient', 'ingredient.thit_vit', 'Thịt vịt'),
                ('ingredient', 'ingredient.trung', 'Trứng'),
                ('ingredient', 'ingredient.sua_dong_vat', 'Sữa và sản phẩm sữa'),
                ('ingredient', 'ingredient.dau_phu', 'Đậu phụ'),
                ('ingredient', 'ingredient.dau_nanh', 'Đậu nành')
            ON CONFLICT (tag_type, code) DO NOTHING;

            -- A legacy category tag brings its meal role along, as the owner form does for new dishes.
            INSERT INTO core.dish_tags (dish_id, tag_id)
            SELECT dt.dish_id, r.id
            FROM core.dish_tags dt
            JOIN core.tags s ON s.id = dt.tag_id AND s.tag_type = 'category'
            JOIN (VALUES
                ('local_sf_main', 'role.mon_chinh'),
                ('local_sf_drink', 'role.do_uong'),
                ('local_sf_dessert', 'role.trang_mieng'),
                ('local_sf_snack', 'role.an_vat')
            ) AS m(src, dst) ON m.src = s.code
            JOIN core.tags r ON r.tag_type = 'meal_role' AND r.code = m.dst
            ON CONFLICT (dish_id, tag_id) DO NOTHING;
        END $$;
        """;

    /// <summary>
    /// Removes the tags this migration added (their dish links cascade) and puts the original tags back on the three
    /// old types, before the CHECK is narrowed again. Ids of removed tags kept in menu_submission_items.tag_ids or Agent
    /// state then point at nothing, as with any deleted tag.
    /// </summary>
    public const string Down = """
        DELETE FROM core.tags
        WHERE tag_type IN ('meal_role', 'cooking_method')
           OR (tag_type = 'ingredient' AND code NOT IN ('pork', 'beef', 'seafood'))
           OR code = 'diet.thuan_chay'
           OR (tag_type = 'category' AND code LIKE 'form.%');

        UPDATE core.tags SET tag_type = 'dietary' WHERE tag_type = 'ingredient' AND code IN ('pork', 'beef', 'seafood');

        UPDATE core.tags AS t SET name = v.name
        FROM (VALUES
            ('meal_time', 'breakfast', 'Bữa sáng'),
            ('meal_time', 'lunch', 'Bữa trưa'),
            ('meal_time', 'dinner', 'Bữa tối'),
            ('meal_time', 'late_night', 'Ăn khuya'),
            ('dietary', 'pork', 'Có thịt heo'),
            ('dietary', 'beef', 'Có thịt bò'),
            ('dietary', 'seafood', 'Có hải sản'),
            ('category', 'local_sf_snack', 'Món ăn vặt')
        ) AS v(tag_type, code, name)
        WHERE t.tag_type = v.tag_type AND t.code = v.code;
        """;
}
